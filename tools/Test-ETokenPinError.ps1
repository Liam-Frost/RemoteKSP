<#
.SYNOPSIS
    用一次 PIN 尝试，查看 eToken CSP 对错误 / 正确 PIN 实际返回的错误码。

.DESCRIPTION
    和服务端 ETokenKeySource 完全相同的调用方式：容器名 "[{{PIN}}]=容器"、
    PROV_RSA_FULL、UseExistingKey | UseMachineKeyStore、SHA256 签名。
    不设置 PersistKeyInCsp（保持默认 true），Dispose 不会删除容器。

    每运行一次、PIN 错误时，token 会扣掉一次重试次数。说明见 docs/Archive/ETOKEN_PIN_ERROR_CHECK.md。

.EXAMPLE
    .\Test-ETokenPinError.ps1 -Container "p11#xxxxxxxxxxxxxxxx"
#>
param(
    [Parameter(Mandatory)][string]$Container,
    [string]$Provider = "eToken Base Cryptographic Provider",
    # 不传时交互输入（不回显）
    [securestring]$Pin
)

$ErrorActionPreference = 'Stop'

# RemoteKSP 识别的两个错误码（src/RemoteKSP.Server/Core/PinProtection.cs）
$known = @{
    '8010006B' = 'SCARD_W_WRONG_CHV（RemoteKSP 识别为 PIN 错误，停用证书）'
    '8010006C' = 'SCARD_W_CHV_BLOCKED（RemoteKSP 识别为已锁定，停用证书）'
}

if (-not $Pin) { $Pin = Read-Host -AsSecureString "PIN（本次只尝试一次）" }
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Pin)
try {
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
} finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
}

$csp = New-Object System.Security.Cryptography.CspParameters
$csp.ProviderType = 1
$csp.ProviderName = $Provider
$csp.KeyContainerName = "[{{$plain}}]=$Container"
$csp.Flags = [System.Security.Cryptography.CspProviderFlags]'UseExistingKey, UseMachineKeyStore'
$plain = $null

Write-Host "提供程序: $Provider"
Write-Host "容器:     $Container"
Write-Host "运行身份: $([Security.Principal.WindowsIdentity]::GetCurrent().Name)"
Write-Host ""

$rsa = $null
try {
    $rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider -ArgumentList $csp
    $sig = $rsa.SignHash((New-Object byte[] 32),
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    Write-Host "结果: 签名成功（$($sig.Length) 字节）。PIN 正确，token 的重试计数应已复位。" -ForegroundColor Green
}
catch {
    Write-Host "结果: 失败。异常链上的 HRESULT：" -ForegroundColor Yellow
    $matched = $null
    for ($e = $_.Exception; $e; $e = $e.InnerException) {
        $hr = '{0:X8}' -f $e.HResult
        $label = $known[$hr]
        if ($label -and -not $matched) { $matched = $label }
        "  0x{0}  {1}: {2}" -f $hr, $e.GetType().Name, $e.Message.Trim() | Write-Host
    }
    Write-Host ""
    if ($matched) {
        Write-Host "RemoteKSP 能识别：$matched" -ForegroundColor Green
    } else {
        Write-Host "RemoteKSP 不能识别这个错误码：服务端会把它当成普通失败，下次签名还会再试同一个 PIN。" -ForegroundColor Red
        Write-Host "请把上面的输出发给维护者，在 PinFailure.Classify 里补上这个错误码。" -ForegroundColor Red
    }
}
finally {
    # 不改 PersistKeyInCsp：默认 true，Dispose 只释放句柄，不会删除容器
    if ($rsa) { $rsa.Dispose() }
}

Write-Host ""
Write-Host "检查容器是否还在："
$listing = certutil -csp $Provider -key 2>&1 | Out-String
if ($listing.Contains($Container)) {
    Write-Host "  容器 $Container 仍在 token 上。" -ForegroundColor Green
} else {
    Write-Host "  在 certutil 输出里没找到 $Container，请手动运行 certutil -csp `"$Provider`" -key 确认。" -ForegroundColor Red
}
