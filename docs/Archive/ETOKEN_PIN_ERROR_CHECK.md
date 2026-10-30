# 验证 eToken 对错误 PIN 返回的错误码

## 为什么要验证

服务端签名时如果 token 拒绝 PIN，会立刻清掉保存的 PIN 并停用证书，之后不再访问 token，
一个错误 PIN 最多消耗 token 一次重试次数。识别依据是异常链上的 HRESULT
（`src/RemoteKSP.Server/Core/PinProtection.cs` 的 `PinFailure.Classify`）：

| HRESULT | 含义 | RemoteKSP 的处理 |
|---|---|---|
| `0x8010006B` | `SCARD_W_WRONG_CHV`，PIN 错误 | 停用证书（PIN 错误） |
| `0x8010006C` | `SCARD_W_CHV_BLOCKED`，重试次数用完 | 停用证书（已锁定） |

这两个值只用模拟 token 测过。token 插着却返回不认识的错误码时也会停用证书（只试一次），
所以即使 SafeNet CSP 用别的错误码表示 PIN 错误，也不会把 token 试锁；但停用原因会显示为"无法识别的错误"，
而且每次停用都要人工重新验证 PIN。验证实际错误码后把它补进 `Classify`，就能显示准确的原因。

同样需要确认：拔掉 token 时 SafeNet CSP 返回什么。RemoteKSP 只在"容器枚举看不到 token"或错误码是
`SCARD_E_NO_SMARTCARD` / `SCARD_E_SHARING_VIOLATION` / `SCARD_E_TIMEOUT` 时允许下次请求再试；
如果拔掉 token 后枚举仍能看到容器、错误码又不在这几个里面，证书会被停用，插回后需要重新验证 PIN。

## 准备

1. **服务端使用最新版本。** 脚本按当前版本服务端的方式访问 token，结果才能直接用于改进 `Classify`。
2. **用一个丢了也没关系的 token。** 测试会故意输错一次 PIN，消耗一次重试次数。
3. 在 SafeNet Authentication Client → Advanced View 里记下：
   - token 的 **剩余 PIN 重试次数**，至少要剩 3 次；
   - 有没有设置 **管理员 PIN（Administrator Password）**。没有管理员 PIN 时，token 锁定后只能重新初始化，私钥会丢失。
4. 停止 RemoteKSP 服务，避免测试期间服务也去访问 token：

   ```bat
   sc.exe stop RemoteKSP
   ```

5. 查出容器名：

   ```bat
   certutil -csp "eToken Base Cryptographic Provider" -key
   ```

## 以 SYSTEM 身份运行

服务以 SYSTEM 身份在 session 0 运行，不能弹出 SafeNet 的 PIN 对话框，
返回的错误码可能和在桌面上运行时不同。**两种身份都测一遍。**

- **当前管理员身份**：在管理员 PowerShell 里直接运行脚本。
- **SYSTEM 身份**：用 Sysinternals 的 PsExec 启动一个 SYSTEM 身份的 PowerShell：

  ```bat
  psexec -s -i powershell.exe
  ```

  在新窗口里运行 `whoami`，应显示 `nt authority\system`。

## 步骤

脚本位于 `tools\Test-ETokenPinError.ps1`。每次运行只尝试一次 PIN，
调用方式和服务端完全一样，并且不会删除容器。

1. **错误 PIN（每种身份各一次）**

   ```powershell
   .\tools\Test-ETokenPinError.ps1 -Container "p11#xxxxxxxxxxxxxxxx"
   ```

   提示输入 PIN 时故意输错。记下输出里的每一行 `0x........`，
   以及最后显示的是"RemoteKSP 能识别"还是"不能识别"。
   如果弹出了 SafeNet 的登录对话框，点"取消"，并记下"弹了对话框"。

2. **正确 PIN**

   再运行一次，输入正确 PIN，应显示"签名成功"。
   成功登录会把 token 的重试计数复位，在 SafeNet Authentication Client 里确认剩余次数恢复了。

3. **确认容器仍在**

   脚本最后会自动用 `certutil` 检查。也可以手动运行准备步骤 5 的命令确认。

4. **恢复服务**

   ```bat
   sc.exe start RemoteKSP
   ```

## 结果怎么判断

- **两种身份都显示"RemoteKSP 能识别"**：保护有效，不需要改代码。
- **任一身份显示"不能识别"**：把该次运行的全部 `0x........` 行发给维护者，
  需要在 `PinFailure.Classify` 里补上对应错误码，并加回归测试。
  不补也不会把 token 试锁（不认识的错误码只试一次就停用），只是停用原因显示不准确。

## 拔掉 token 时的错误码

1. 拔掉 eToken，按上面的步骤以 SYSTEM 身份运行脚本，输入**正确** PIN（token 不在，不会消耗重试次数）。
2. 记下输出里的 `0x........` 行，以及最后"检查容器是否还在"的结果。
3. 如果显示的是 `0x8010000C`（`SCARD_E_NO_SMARTCARD`），或者容器检查显示"没找到"，RemoteKSP 会把它当成"没插 token"，
   插回后自动恢复；否则把输出发给维护者。
- **SYSTEM 身份下弹了对话框，或返回"操作被取消"之类的错误**：同样发给维护者。
  这说明服务在 session 0 访问 token 的行为和桌面上不同，需要单独处理。

## 不要做的事

- 不要连续多次输错 PIN 来"多测几次"。每次都会消耗真实的重试次数。
- 不要自己写测试代码时设置 `PersistKeyInCsp = false`。它会在释放对象时删除 token 上的私钥。
