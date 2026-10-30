# 安全协议与回归测试

## eToken 密钥容器

服务端打开 eToken 容器时不设置 `RSACryptoServiceProvider.PersistKeyInCsp = false`。设为 `false` 后，对象释放时 .NET 会调用 `CryptAcquireContext(CRYPT_DELETEKEYSET)`；容器名里带着 PIN（`[{{PIN}}]=容器`），eToken CSP 不会再要求认证，直接删除容器。回归测试用软件 CSP 和服务端相同的容器名格式签名后检查容器仍然存在，改动这一点会让测试失败。自己写测试代码访问 eToken 时也要注意这一点。

## TLS 服务端身份验证

首次配对接受自签名 TLS 证书，由用户核对包含 TLS 指纹和双方随机数的验证码。后续配对轮询固定首次看到的证书，管理客户端与 Native KSP 固定已保存的 `TlsThumbprint`。

Native KSP 在 WinHTTP 的 `SENDING_REQUEST` 通知中读取当前连接的服务端证书，匹配配对指纹后才允许发送 HTTP 请求头和正文。证书不匹配、没有指纹、指纹非法或使用 HTTP 时直接拒绝。自签名证书通过固定指纹建立信任，不依赖公共 CA。这里是服务端身份验证，不是双向 TLS。

配对客户端、管理客户端和 Native KSP 均禁止自动重定向，防止请求被转发到其他地址。

## 请求认证 V2

认证头为 `Authorization: RKSP-HMAC-V2 <base64 HMAC-SHA256>`，同时携带 `X-Client-Id`、`X-Timestamp`、`X-Hash-Algorithm` 和 `X-Request-Nonce`。

nonce 使用密码学随机数生成器产生的 16 字节随机值，编码为 32 个小写十六进制字符。每次请求生成新 nonce，即使同一秒内执行相同操作也可以正常处理。

HMAC 规范串采用 UTF-8，各字段之间为单个 LF，末尾没有 LF：

```text
METHOD
PATH
TIMESTAMP
HASHALG
CLIENTID
BODY
NONCE
```

`BODY` 是实际发送的 base64 文本，空正文为空字符串。服务端先验证时间窗口与 HMAC，再以 `(clientId, nonce)` 原子登记请求；并发重放只有一个请求能够通过。失败的 HMAC 不占用 nonce。

允许的时钟误差为前后 300 秒。nonce 保留到该请求时间戳完全超出有效窗口，因此客户端时钟领先时缓存可能保留最多约 600 秒。缓存最多保存 100,000 个有效请求，满时拒绝新请求，不淘汰仍有效的记录。

防重放缓存属于单个服务进程，重启后不保留，也不跨实例共享。当前实现适用于单实例部署，不提供跨重启或多实例的持久防重放保证。TLS 仍必须启用；不要将此机制当作可以安全公开请求凭据的理由。

## 配对状态与凭据

请求有效期为 5 分钟，在批准、查询及领取时直接检查，不依赖 GUI 刷新或后续请求清理。

凭据登记和领取在同一个锁内完成。登记成功后请求进入 `consumed` 状态，清除待领取密钥；之后轮询只返回状态。状态查询返回副本且不包含密钥，调用方不能通过修改返回对象改变配对状态。登记失败时不消费领取资格。

一次性领取意味着成功响应在网络中丢失后不能再次领取；客户端应重新配对。重新配对不会仅凭客户端自报的机器名称撤销其他客户端，同名机器不能作为身份依据；需要通过服务端客户端列表撤销旧凭据。

## 服务程序位置

服务以 SYSTEM 身份运行，谁能修改服务程序所在目录，谁就能以 SYSTEM 执行代码。安装、重新安装和「更新服务程序」都会先把当前 exe 复制到 `%ProgramFiles%\RemoteKSP\RemoteKSP.Server.exe`，再让服务指向这个副本；注册时 exe 路径单独加引号，带空格的路径不会被拆开。目标目录对 `Users`、`Authenticated Users`、`Everyone` 等普通用户可写时拒绝部署。

更新时先完整复制到临时文件，再把旧 exe 改名让位、换上新文件；任何一步失败都恢复旧文件，服务按原状态启动。旧版本直接用下载或发布目录注册的服务，设置页会提示迁移。

卸载服务会删除服务、防火墙规则和 `%ProgramFiles%\RemoteKSP` 里的服务程序，默认保留数据目录。选择同时删除数据目录时会再确认一次；目录里有联接或符号链接时拒绝删除，不会跟着链接删到别处。

## 服务端数据目录

`C:\ProgramData\RemoteKSP` 保存 eToken PIN、PFX 密码、客户端 HMAC 密钥和 TLS 私钥，均用 DPAPI `LocalMachine` 范围加密。这个范围本机任何进程都能解密，所以保护只能依赖目录 ACL。

服务每次启动时把目录收紧为只允许 `SYSTEM` 和 `Administrators` 访问，去掉从 `ProgramData` 继承的 `Users` 读权限，把已有子项重置为只继承这两条，所有者改为 `Administrators`。收紧失败时服务拒绝启动。

普通用户能在 `ProgramData` 下新建目录。如果数据目录本身或其中任何一项是联接或符号链接，服务拒绝使用并在启动日志里给出路径，不写入、不改动链接和它指向的目录；管理员确认后删除链接本身即可。

TLS 证书 `server.pfx` 存在时一直使用，不按有效期轮换（客户端和 Native KSP 按指纹固定证书，不检查有效期）。文件存在但读不出来时服务报错停止，不会自动生成新证书，因为换证书后所有客户端都要重新配对。`config.json` 里的 TLS 指纹丢失时以 `server.pfx` 为准自动补回。

确实要换证书时，在服务端 GUI「设置 → TLS 证书」里点「重新生成 TLS 证书」。服务运行时由服务生成，新连接立即使用新证书，不用重启服务；服务因证书损坏起不来时，GUI 先停止服务、自己生成，再启动服务。原 `server.pfx` 复制为 `server.pfx.bak-<时间>` 保留，想恢复时停止服务、把备份改回 `server.pfx` 再启动。生成后每台客户端都要移除该服务端并重新配对。

配置文件先写临时文件再整体替换，避免断电后留下写了一半的文件。`certificates.json` 或 `clients.json` 解析失败时，原文件改名为 `*.corrupt-<时间>` 保留，服务以空列表启动并在活动日志里记录错误，不会用空列表覆盖原文件。

## 客户端证书存储

客户端只改动和删除私钥指向 `Remote KSP` 的证书。如果 `LocalMachine\My` 中已有同指纹、但私钥不由 Remote KSP 提供（或只有公钥）的证书，同步时跳过它，解绑时也保留它。这样在服务端机器上运行客户端，不会破坏服务端 eToken 或证书存储来源依赖的证书。

"更新证书"收到 401 时不再自动删除配对。401 也可能来自时钟相差超过 5 分钟、服务端配对记录损坏后待恢复等情况，删掉的配对密钥无法找回。客户端列出可能原因，由用户确认后才移除服务端并解绑证书。

## eToken PIN

token 拒绝一个 PIN 后，同一进程内同一容器的所有密钥源对象都记住这个 PIN（只保存 SHA-256）。GUI 在签名途中改动证书、重新加载出的新对象不会再拿它访问 token。PIN 验证成功后清除记录。

签名失败时，只有确定还没走到校验 PIN 的情况才允许下一个请求再试：

| 情况 | 判断方式 | 处理 |
|---|---|---|
| token 没插 | 用 `CRYPT_VERIFYCONTEXT` 枚举容器名（不打开私钥、不用 PIN），看不到该容器 | 本次失败，下次请求再试，PIN 保留 |
| 卡被占用、等卡超时、读卡器里没卡 | `SCARD_E_SHARING_VIOLATION`、`SCARD_E_TIMEOUT`、`SCARD_E_NO_SMARTCARD` | 本次失败，下次请求再试，PIN 保留 |
| PIN 错误、已锁 | `SCARD_W_WRONG_CHV`、`SCARD_W_CHV_BLOCKED` | 立即停用，删除保存的 PIN |
| token 插着，错误码不认识 | 其他错误 | 立即停用，删除保存的 PIN（可能是 CSP 用别的错误码表示 PIN 错误），需要在 GUI 里重新验证 PIN |

加载 eToken 证书只读 `LocalMachine\My` 里的公钥证书，不按容器枚举结果拦截：服务启动时没插 token 也照样加载，插上后下一个签名请求直接可用。枚举结果只用于状态显示。

## 证书存储来源

客户端绑定到 Remote KSP 的证书在证书存储里也显示为"有私钥"，但私钥在别的服务端上。服务端的「证书存储」来源不列出、也拒绝使用私钥指向 Remote KSP 的证书，否则签名会被转给别的服务端，配对的是本机时会一直绕回自己。

## 升级

1. 同步更新服务端、客户端和 x64/x86 KSP DLL。
2. 服务端：以管理员打开新版本 exe，在「设置 → Windows 服务」点「更新服务程序」。数据目录、证书、PIN 和客户端配对不受影响。
3. 客户端会比较已安装 DLL 与内嵌 DLL 的内容，有差异时在概览页提示更新 KSP。已加载旧 DLL 的签名程序需要关闭后重新打开。
4. 服务端更换 TLS 证书或修改 HTTPS 端口后，客户端需要移除该服务端并重新配对。
5. 先在测试环境验证签名流程，再更新实际服务。

## 回归测试

Windows 上安装 .NET 9 SDK 和 Visual Studio C++ 工具后，在仓库根目录执行：

```powershell
.\native\ksp\build.cmd
.\tests\native\build.cmd
dotnet restore tests/SecurityRegression/SecurityRegression.csproj
dotnet build tests/SecurityRegression/SecurityRegression.csproj -c Release --no-restore -m:1
dotnet run --project tests/SecurityRegression/SecurityRegression.csproj -c Release --no-build --no-restore
```

测试使用合成凭据、临时自签名证书和本机回环 HTTPS 服务，不安装 KSP、不修改 HKLM、不访问真实签名密钥。Windows Schannel 使用的临时证书私钥在证书释放后删除。隔离的服务端测试数据写入测试项目的 `bin` 目录。

覆盖正常请求、篡改 nonce、无效 HMAC、时间窗口边界、并发重放、缓存容量与过期回收、过期配对、并发一次性领取，以及真实 HTTP 路由的凭据登记和认证。x64/x86 原生探针使用生产 HTTP 实现，验证正确指纹可通信、错误指纹在发送 HTTP 请求前失败、缺失指纹与 HTTP 被拒绝。

密钥与数据安全部分还覆盖：

- **私钥**：用软件 CSP 和服务端相同的容器名格式签名后，密钥容器仍然存在。
- **数据目录**：收紧后只剩 `SYSTEM` 和 `Administrators`；数据目录本身或其中有联接时拒绝使用，不改动链接目标。
- **配置文件**：原子写入；损坏文件保留，不被覆盖。
- **TLS 证书**：已有 `server.pfx` 一直沿用，读不出来时报错、不重新生成；重新生成时原文件逐字节备份，Kestrel 不重启即对新连接使用新证书。
- **eToken PIN**：
  - 8 个并发的错误 PIN 请求只访问 token 一次。
  - 签名途中重新加载，不会再试同一个错误 PIN。
  - 没插 token 时多次请求都不会走到 PIN 校验，PIN 保留，插上后自动恢复。
  - 卡忙可以重试；token 插着、错误码不认识时只校验一次 PIN 就停用。
- **证书存储来源**：私钥指向 Remote KSP 的证书不列出，签名时拒绝。
- **客户端**：在 `CurrentUser\My` 中不接管、不删除非 Remote KSP 安装的证书。
- **服务程序部署**：
  - 带空格路径的 `binPath` 按 Windows 规则解析为一个整体。
  - 更新失败时回滚，拒绝部署到普通用户可写的目录。
  - 卸载时只删自己的文件；删除数据目录时遇到联接会拒绝。

测试环境必须允许本机回环网络。测试使用模拟 eToken，不覆盖真实 HSM/eToken 硬件或第三方签名应用；服务安装、迁移、改端口需要管理员权限，也不在自动化测试内。测试不代替全面安全审计。真实 eToken 对错误 PIN 和拔卡的错误码需要按 [ETOKEN_PIN_ERROR_CHECK.md](Archive/ETOKEN_PIN_ERROR_CHECK.md) 手动验证。
