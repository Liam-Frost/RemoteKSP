# RemoteKSP 多服务端多证书架构设计

## 概述

将 RemoteKSP 从单服务端单证书架构升级为支持：
- **服务端**：管理多个证书（eToken、证书存储、PFX、测试证书），控制每个客户端的证书访问权限
- **客户端**：连接多个服务端，管理来自不同服务端的多个证书，应用可使用任意已绑定的证书签名

## 设计目标

1. **服务端灵活性**：一台服务器可分享多个签名证书（不同用途、不同有效期、备份证书）
2. **客户端灵活性**：一台客户机可连接多个服务器，获取不同组织/团队的签名证书
3. **权限细粒度**：服务端可以控制每个客户端能访问哪些证书
4. **向后兼容**：现有单证书配置自动迁移，KSP 协议保持兼容

## 核心概念

### 证书（Certificate）
一个可用于签名的密钥对，来源可以是：
- **eToken**：物理 USB Token，需要 PIN
- **证书存储**：Windows 证书存储（LocalMachine 或 CurrentUser）
- **PFX 文件**：PKCS#12 文件，需要密码
- **测试证书**：自动生成的虚拟证书，仅用于测试配对流程

### 服务端（Server）
提供证书签名服务的后端，每个服务端：
- 管理一个或多个证书
- 维护已配对的客户端列表
- 控制每个客户端的证书访问权限（默认全部可用）

### 客户端（Client）
安装了 Remote KSP 的 Windows 机器，每个客户端：
- 可配对多个服务端
- 从每个服务端获取允许使用的证书列表
- 本地证书存储（LocalMachine\My）中每个证书通过 KSP 绑定到对应的服务端

## 架构设计

### 1. 服务端数据结构

#### 1.1 证书注册表 `certificates.json`

```json
{
  "certificates": [
    {
      "id": "cert-001",
      "name": "代码签名证书 2024",
      "thumbprint": "a1b2c3d4...",
      "sourceType": "etoken",
      "isShared": true,
      "addedAt": "2024-10-19T10:00:00Z",
      
      // sourceType = "etoken"
      "cspProvider": "eToken Base Cryptographic Provider",
      "keyContainer": "te-12345678",
      "pinEncrypted": "AQAAANCMnd8BFd..."
    },
    {
      "id": "cert-002",
      "name": "测试证书",
      "thumbprint": "e5f6a7b8...",
      "sourceType": "test",
      "isShared": true,
      "addedAt": "2024-10-19T09:00:00Z"
    },
    {
      "id": "cert-003",
      "name": "备份签名证书",
      "thumbprint": "1234abcd...",
      "sourceType": "store",
      "isShared": false,
      "storeLocation": "LocalMachine",
      "addedAt": "2024-10-18T15:30:00Z"
    },
    {
      "id": "cert-004", 
      "name": "开发用 PFX",
      "thumbprint": "9876fedc...",
      "sourceType": "pfx",
      "isShared": true,
      "pfxPath": "dev-cert.pfx",
      "pfxPasswordEncrypted": "AQAAANCMnd8...",
      "addedAt": "2024-10-17T08:00:00Z"
    }
  ]
}
```

#### 1.2 客户端存储 `clients.json`（扩展）

```json
[
  {
    "id": "client-uuid-001",
    "machineName": "DEV-PC-01",
    "ipAddress": "192.168.1.100",
    "secretEncrypted": "AQAAANCMnd8...",
    "pairedAt": "2024-10-19T10:30:00Z",
    "lastSeen": "2024-10-19T14:20:00Z",
    "isRevoked": false,
    "allowedCertIds": null  // null = 全部允许，[] = 禁止所有，["cert-001", "cert-002"] = 仅这些
  }
]
```

### 2. 客户端数据结构

#### 2.1 服务端注册表 `HKLM\SOFTWARE\RemoteKSP\Servers`

```
HKLM\SOFTWARE\RemoteKSP\Servers\
  ├─ server-001\
  │   ├─ ServerUrl       REG_SZ  "https://sign1.company.com:8443"
  │   ├─ ServerName      REG_SZ  "公司代码签名服务器"
  │   ├─ ClientId        REG_SZ  "client-uuid-001"
  │   ├─ SecretProtected REG_BINARY (DPAPI encrypted)
  │   └─ TlsThumbprint   REG_SZ  "abc123..."
  │
  ├─ server-002\
  │   ├─ ServerUrl       REG_SZ  "https://sign2.partner.com:8443"
  │   ├─ ServerName      REG_SZ  "合作伙伴签名服务器"
  │   ├─ ClientId        REG_SZ  "client-uuid-002"
  │   ├─ SecretProtected REG_BINARY
  │   └─ TlsThumbprint   REG_SZ  "def456..."
```

#### 2.2 证书-服务端映射 `HKLM\SOFTWARE\RemoteKSP\Certificates`

```
HKLM\SOFTWARE\RemoteKSP\Certificates\
  ├─ a1b2c3d4...\ (thumbprint)
  │   ├─ ServerId  REG_SZ  "server-001"
  │   ├─ Name      REG_SZ  "代码签名证书 2024"
  │   └─ AddedAt   REG_SZ  "2024-10-19T10:35:00Z"
  │
  ├─ e5f6a7b8...\
  │   ├─ ServerId  REG_SZ  "server-001"
  │   ├─ Name      REG_SZ  "测试证书"
  │   └─ AddedAt   REG_SZ  "2024-10-19T10:35:00Z"
  │
  ├─ 9876fedc...\
  │   ├─ ServerId  REG_SZ  "server-002"
  │   ├─ Name      REG_SZ  "合作伙伴签名证书"
  │   └─ AddedAt   REG_SZ  "2024-10-19T11:00:00Z"
```

### 3. 协议变更

#### 3.1 `/info` 保持匿名，只做兼容

`/info` 不需要认证，服务端无法区分请求方，因此**不能**用它返回按客户端过滤的证书列表。
保留现有的 `thumbprint` 字段（取分享列表里的第一个证书），旧客户端照常工作；新增 `multi-cert` 特性标记，供新客户端判断是否走新接口。

```json
{
  "service": "RemoteKSP",
  "version": "3.0",
  "features": ["pairing", "hmac-auth", "test-mode", "multi-cert"],
  "thumbprint": "a1b2c3d4...",
  "mode": "production"
}
```

#### 3.2 新增 `GET /certs`（需要 HMAC 认证）

返回**当前客户端被允许使用**的证书列表（分享列表 ∩ 该客户端的 `allowedCertIds`）：

```json
{
  "certificates": [
    {
      "thumbprint": "a1b2c3d4...",
      "name": "代码签名证书 2024",
      "subject": "CN=Company Name",
      "notAfter": "2025-12-31T23:59:59Z"
    },
    {
      "thumbprint": "e5f6a7b8...",
      "name": "测试证书",
      "subject": "CN=RemoteKSP Test",
      "notAfter": "2099-12-31T23:59:59Z"
    }
  ]
}
```

每个条目再加 `isTest` 字段，客户端据此把测试证书标出来（测试证书只能验证配对和下发，签名会被拒绝）。

#### 3.3 现有端点改为按指纹路由并检查权限

- `GET /cert/{thumbprint}`：目前允许匿名访问。改造后，带认证头的请求要检查权限；匿名请求只允许下载 `/info` 里那个兼容证书，这样分享列表不会被未配对的机器枚举出来
- `POST /sign/{thumbprint}`：按指纹找到对应证书，再检查该客户端是否有权使用；无权返回 403，指纹不存在返回 404
- 旧版签名逻辑会忽略 URL 里的指纹、直接用全局唯一证书，这部分要改成真正按指纹查找

### 4. Native KSP 改造

#### 4.1 `EnumKeys` 逻辑

```cpp
// 旧逻辑：返回单个指纹
WCHAR thumbprint[128];
LoadSingleThumbprint(thumbprint);
*ppKeyName = AllocKeyName(thumbprint);

// 新逻辑：枚举所有证书
EnumCertificates(ppEnumState, ppKeyName); // 遍历 HKLM\SOFTWARE\RemoteKSP\Certificates
```

#### 4.2 `OpenKey` 逻辑

```cpp
// 旧逻辑：从固定位置读取 ServerUrl
LoadConfig(prov->serverUrl, prov->secret, prov->clientId);

// 新逻辑：从证书映射表读取对应的 ServerId，再读取服务端配置
WCHAR serverId[64];
LoadCertificateServerId(thumbprint, serverId);
LoadServerConfig(serverId, key->serverUrl, key->secret, key->clientId);
```

#### 4.3 数据结构调整

现在 `serverUrl`、`secret`、`clientId` 都放在 `RemoteKspProvider` 里，打开 provider 时加载一次，等于一个 provider 只能对应一个服务端。
多服务端模式下，同一个 provider 句柄要能打开来自不同服务端的密钥，所以这三个字段必须挪到 `RemoteKspKey`，在 `OpenKey` 时按证书加载。
`HttpPostSign` 的参数也要从 `RemoteKspProvider*` 改为 `RemoteKspKey*`。

## 实现计划

### 阶段 1：服务端多证书基础（2-3天）

**目标**：服务端支持管理多个证书，端点支持按指纹路由

1. 创建 `CertificateRegistry.cs` 和 `certificates.json`
2. 重构 `KeySourceManager` 为工厂模式，返回 `IKeySource` 接口
3. 实现 `IKeySource` 的四个实现：`ETokenKeySource`, `StoreKeySource`, `PfxKeySource`, `TestKeySource`
4. 新增需要认证的 `GET /certs`，`/info` 保持兼容
5. 改造 `/cert/{thumbprint}` 和 `/sign/{thumbprint}`，支持按指纹查找多个证书

### 阶段 2：服务端 GUI 证书管理（2天）

**目标**：删除旧的"密钥源配置"，新增"证书管理"标签页

1. 新建 `CertificateManagementTab.cs`：
   - 证书列表（DataGrid）
   - "添加证书"对话框（`AddCertificateDialog.cs`）
   - "编辑" / "删除" 按钮
   - eToken PIN 输入（选中 eToken 证书时显示）
2. 迁移工具：读取旧的 `config.json`，转换为第一个证书条目

### 阶段 3：服务端权限管理（1天）

**目标**：控制每个客户端的证书访问权限

1. `ClientStore` 添加 `AllowedCertIds` 字段
2. GUI "客户端"标签页添加"管理权限"按钮
3. 权限对话框：CheckBox 列表选择允许的证书
4. 端点添加权限检查逻辑

### 阶段 4：客户端多服务端基础（2-3天）

**目标**：客户端支持连接多个服务端

1. 重构 `ClientRegistry.cs` 为多服务端结构（`Servers\{id}` 注册表项）
2. 新建 `ServerManager.cs` 管理服务端列表
3. 改造 `PairingClient` 支持指定服务端ID
4. 改造 `CertificateBinder` 支持多证书绑定（`Certificates\{thumbprint}` 映射表）
5. 新增"更新证书"功能：从 `GET /certs` 获取列表，批量下载并绑定；服务端撤销权限的证书要从本地解绑

### 阶段 5：客户端 GUI 多服务端管理（2天）

**目标**：GUI 管理多个服务端和证书

1. 重构主窗口为 TabControl，三个标签页：
   - **服务端管理**：列表显示已配对的服务端，"添加服务端"、"移除服务端"
   - **证书管理**：列表显示所有证书（ServerId, Name, Thumbprint, Status），"更新证书"、"移除证书"
   - **连接测试**：选择服务端和证书进行测试
2. "添加服务端"对话框：输入 URL 和名称，配对流程
3. "更新证书"：选择服务端，从该服务端刷新证书列表

### 阶段 6：Native KSP 多服务端支持（2天）

**目标**：KSP DLL 支持枚举和使用来自多个服务端的证书

1. 改造注册表读取逻辑：
   - `LoadCertificateServerId()` - 从证书映射表查 ServerId
   - `LoadServerConfig()` - 读取指定服务端的配置
2. 改造 `EnumKeys()`：遍历 `Certificates` 键，返回所有证书
3. 改造 `OpenKey()`：根据容器名（thumbprint）查找对应的服务端
4. 测试：多个应用同时使用不同服务端的证书

### 阶段 7：测试与文档（1-2天）

**目标**：端到端测试，更新用户文档

1. 场景测试：
   - 单服务端多证书
   - 多服务端单证书
   - 多服务端多证书
   - 权限控制
   - 证书更新
2. 更新 README.md 和用户手册
3. 迁移指南（从单证书版本升级）

## 估算时间

- **总工作量**：12-16 天
- **关键路径**：阶段 1 → 阶段 4 → 阶段 6
- **可并行**：阶段 2/3 与 阶段 5

## 风险与缓解

### 风险 1：注册表结构变更导致现有客户端失效
**缓解**：
- 保留旧的注册表位置作为兼容模式
- 首次启动时自动迁移旧配置

### 风险 2：Native KSP 改造引入稳定性问题
**缓解**：
- 充分的单元测试和集成测试
- 发布前在多个 Windows 版本上测试（Win10/11, Server 2019/2022）

### 风险 3：多证书导致用户界面复杂化
**缓解**：
- 默认行为保持简单（单服务端单证书时与旧版一致）
- 高级功能放在独立的标签页/对话框

## 兼容性

### 协议兼容性
- 新版服务端兼容旧版客户端：旧客户端只认 `/info.thumbprint`，也就是分享列表里的第一个证书
- 新版客户端兼容旧版服务端：`/info.features` 里没有 `multi-cert` 时，降级为单证书模式，只用 `/info.thumbprint`

### 安全注意

- 服务以 SYSTEM 运行，存储位置选 CurrentUser 时读到的是 SYSTEM 的证书存储。GUI 里要提示用户，把证书导入 LocalMachine\My
- 两台服务端可能分享同一张证书（指纹相同）。客户端的映射表以指纹为键，一个指纹只能绑定一个服务端；添加时发现指纹已被别的服务端绑定，要让用户选择保留哪一个

### 配置迁移
- 服务端：首次启动时读取旧 `config.json`，生成第一个证书条目
- 客户端：首次启动时读取旧注册表位置，迁移为 `server-001`

## 未来扩展

1. **证书自动轮换**：临近过期时自动切换到备份证书
2. **签名审计**：记录每次签名的时间、证书、客户端、内容哈希
3. **负载均衡**：客户端自动选择响应最快的服务端
4. **Web 管理界面**：通过浏览器管理服务端（替代 WPF GUI）
