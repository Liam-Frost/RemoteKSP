#include "ksp.h"
#include <stdio.h>
#include <stdlib.h>
#include <time.h>
#include <wctype.h>

// 指纹只允许十六进制字符：它会被拼进注册表路径和 URL，不能带 '\\'、'/' 之类
bool IsValidThumbprint(const WCHAR* thumbprint)
{
    if (!thumbprint)
        return false;
    size_t len = wcslen(thumbprint);
    if (len == 0 || len > 64)
        return false;
    for (size_t i = 0; i < len; i++) {
        if (!iswxdigit(thumbprint[i]))
            return false;
    }
    return true;
}

// 客户端 GUI 迁移/配对后会创建 Certificates 键；不存在说明还是旧版单服务端配置
bool HasCertificateMap()
{
    HKEY hKey;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, REMOTEKSP_REG_CERTS, 0, KEY_READ | KEY_WOW64_64KEY, &hKey) != ERROR_SUCCESS)
        return false;
    RegCloseKey(hKey);
    return true;
}

// 从一个注册表键读取 ServerUrl / ClientId / SecretProtected（DPAPI 机器范围加密）
static bool LoadServerValues(const WCHAR* regPath, WCHAR* serverUrl, size_t urlLen,
    char* secret, size_t secretLen, char* clientId, size_t clientIdLen,
    WCHAR* tlsThumbprint, size_t tlsLen)
{
    HKEY hKey;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, regPath, 0, KEY_READ | KEY_WOW64_64KEY, &hKey) != ERROR_SUCCESS)
        return false;

    DWORD type, size = (DWORD)(urlLen * sizeof(WCHAR));
    bool ok = RegQueryValueExW(hKey, L"ServerUrl", NULL, &type, (BYTE*)serverUrl, &size) == ERROR_SUCCESS
        && type == REG_SZ;

    size = (DWORD)clientIdLen;
    ok = ok && RegQueryValueExA(hKey, "ClientId", NULL, &type, (BYTE*)clientId, &size) == ERROR_SUCCESS
        && type == REG_SZ;

    size = (DWORD)(tlsLen * sizeof(WCHAR));
    ok = ok && RegQueryValueExW(hKey, L"TlsThumbprint", NULL, &type, (BYTE*)tlsThumbprint, &size) == ERROR_SUCCESS
        && type == REG_SZ && size == 41 * sizeof(WCHAR) && tlsThumbprint[40] == 0
        && wcslen(tlsThumbprint) == 40 && IsValidThumbprint(tlsThumbprint);

    BYTE encryptedSecret[512];
    size = sizeof(encryptedSecret);
    ok = ok && RegQueryValueExA(hKey, "SecretProtected", NULL, &type, encryptedSecret, &size) == ERROR_SUCCESS
        && type == REG_BINARY;
    RegCloseKey(hKey);

    if (!ok)
        return false;

    DATA_BLOB input = { size, encryptedSecret };
    DATA_BLOB output = { 0 };
    if (!CryptUnprotectData(&input, NULL, NULL, NULL, NULL, 0, &output))
        return false;

    ok = output.cbData < secretLen;
    if (ok) {
        memcpy(secret, output.pbData, output.cbData);
        secret[output.cbData] = 0;
    }
    SecureZeroMemory(output.pbData, output.cbData);
    LocalFree(output.pbData);
    return ok;
}

// Certificates\{thumbprint}\ServerId
static bool LoadCertificateServerId(const WCHAR* thumbprint, WCHAR* serverId, size_t serverIdLen)
{
    WCHAR path[256];
    swprintf_s(path, 256, L"%s\\%s", REMOTEKSP_REG_CERTS, thumbprint);

    HKEY hKey;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, path, 0, KEY_READ | KEY_WOW64_64KEY, &hKey) != ERROR_SUCCESS)
        return false;

    DWORD type, size = (DWORD)(serverIdLen * sizeof(WCHAR));
    bool ok = RegQueryValueExW(hKey, L"ServerId", NULL, &type, (BYTE*)serverId, &size) == ERROR_SUCCESS
        && type == REG_SZ;
    RegCloseKey(hKey);

    // ServerId 同样会拼进注册表路径
    return ok && serverId[0] != 0 && wcschr(serverId, L'\\') == NULL;
}

bool LoadKeyConfig(const WCHAR* thumbprint, WCHAR* serverUrl, size_t urlLen,
    char* secret, size_t secretLen, char* clientId, size_t clientIdLen,
    WCHAR* tlsThumbprint, size_t tlsLen)
{
    if (!IsValidThumbprint(thumbprint))
        return false;

    if (!HasCertificateMap()) {
        // 旧版：根键下只有一台服务端
        return LoadServerValues(REMOTEKSP_REG_ROOT, serverUrl, urlLen, secret, secretLen, clientId, clientIdLen,
            tlsThumbprint, tlsLen);
    }

    WCHAR serverId[64] = { 0 };
    if (!LoadCertificateServerId(thumbprint, serverId, 64))
        return false;

    WCHAR path[256];
    swprintf_s(path, 256, L"%s\\%s", REMOTEKSP_REG_SERVERS, serverId);
    return LoadServerValues(path, serverUrl, urlLen, secret, secretLen, clientId, clientIdLen,
        tlsThumbprint, tlsLen);
}

struct TlsPinContext {
    const WCHAR* thumbprint;
    bool rejected;
    bool checked;
};

// SENDING_REQUEST occurs after the TLS handshake, before HTTP headers/body are sent.
// Synchronous WinHTTP permits closing the handle from this informational callback.
static void CALLBACK CheckTlsPin(HINTERNET request, DWORD_PTR context, DWORD status, LPVOID, DWORD)
{
    if (status != WINHTTP_CALLBACK_STATUS_SENDING_REQUEST || !context)
        return;
    TlsPinContext* pin = (TlsPinContext*)context;
    PCCERT_CONTEXT cert = NULL;
    DWORD size = sizeof(cert);
    bool matches = false;
    if (WinHttpQueryOption(request, WINHTTP_OPTION_SERVER_CERT_CONTEXT, &cert, &size)) {
        BYTE hash[20];
        DWORD hashSize = sizeof(hash);
        if (CertGetCertificateContextProperty(cert, CERT_SHA1_HASH_PROP_ID, hash, &hashSize)
            && hashSize == sizeof(hash)) {
            WCHAR actual[41];
            for (DWORD i = 0; i < hashSize; ++i)
                swprintf_s(actual + i * 2, 41 - i * 2, L"%02x", hash[i]);
            matches = _wcsicmp(actual, pin->thumbprint) == 0;
        }
        CertFreeCertificateContext(cert);
    }
    pin->checked = true;
    if (!matches) {
        pin->rejected = true;
        WinHttpCloseHandle(request);
    }
}

// Compute HMAC-SHA256
static SECURITY_STATUS ComputeHmac(const char* secret, const char* message, BYTE* hash, DWORD* pcbHash)
{
    BCRYPT_ALG_HANDLE hAlg;
    if (!BCRYPT_SUCCESS(BCryptOpenAlgorithmProvider(&hAlg, BCRYPT_SHA256_ALGORITHM, NULL, BCRYPT_ALG_HANDLE_HMAC_FLAG)))
        return NTE_INVALID_PARAMETER;

    BCRYPT_HASH_HANDLE hHash;
    NTSTATUS st = BCryptCreateHash(hAlg, &hHash, NULL, 0,
        (PUCHAR)secret, (ULONG)strlen(secret), 0);

    if (st < 0) {
        BCryptCloseAlgorithmProvider(hAlg, 0);
        return NTE_INVALID_PARAMETER;
    }

    BCryptHashData(hHash, (PUCHAR)message, (ULONG)strlen(message), 0);
    BCryptFinishHash(hHash, hash, *pcbHash, 0);

    BCryptDestroyHash(hHash);
    BCryptCloseAlgorithmProvider(hAlg, 0);
    return ERROR_SUCCESS;
}

// Generic authenticated HTTP request
static SECURITY_STATUS HttpRequest(
    const WCHAR* serverUrl,
    const WCHAR* tlsThumbprint,
    const char* clientId,
    const char* secret,
    const char* method,
    const WCHAR* path,
    const BYTE* body,
    DWORD cbBody,
    const char* hashAlgorithm,
    BYTE** ppResponse,
    DWORD* pcbResponse)
{
    URL_COMPONENTS uc = { sizeof(uc) };
    WCHAR host[256], urlPath[512];
    uc.lpszHostName = host;
    uc.dwHostNameLength = 256;
    uc.lpszUrlPath = urlPath;
    uc.dwUrlPathLength = 512;

    WCHAR fullUrl[1024];
    swprintf_s(fullUrl, 1024, L"%s%s", serverUrl, path);

    if (!WinHttpCrackUrl(fullUrl, 0, 0, &uc) || uc.nScheme != INTERNET_SCHEME_HTTPS ||
        !tlsThumbprint || wcslen(tlsThumbprint) != 40 || !IsValidThumbprint(tlsThumbprint))
        return NTE_INVALID_PARAMETER;

    BYTE random[16];
    if (!BCRYPT_SUCCESS(BCryptGenRandom(NULL, random, sizeof(random), BCRYPT_USE_SYSTEM_PREFERRED_RNG)))
        return NTE_FAIL;
    char nonce[33];
    for (size_t i = 0; i < sizeof(random); ++i)
        sprintf_s(nonce + i * 2, sizeof(nonce) - i * 2, "%02x", random[i]);

    char timestamp[32];
    sprintf_s(timestamp, 32, "%lld", (long long)time(NULL));

    char bodyBase64[4096] = "";
    if (body && cbBody > 0) {
        DWORD cbB64 = 4096;
        if (!CryptBinaryToStringA(body, cbBody, CRYPT_STRING_BASE64 | CRYPT_STRING_NOCRLF, bodyBase64, &cbB64))
            return NTE_INVALID_PARAMETER;
        bodyBase64[cbB64] = 0;
    }

    char pathA[512];
    WideCharToMultiByte(CP_UTF8, 0, path, -1, pathA, 512, NULL, NULL);

    char canonical[4096];
    sprintf_s(canonical, 4096, "%s\n%s\n%s\n%s\n%s\n%s\n%s",
        method, pathA, timestamp, hashAlgorithm, clientId, bodyBase64, nonce);

    BYTE hmac[32];
    DWORD cbHmac = 32;
    if (ComputeHmac(secret, canonical, hmac, &cbHmac) != ERROR_SUCCESS)
        return NTE_INVALID_PARAMETER;

    char hmacB64[128];
    DWORD cbHmacB64 = 128;
    if (!CryptBinaryToStringA(hmac, cbHmac, CRYPT_STRING_BASE64 | CRYPT_STRING_NOCRLF, hmacB64, &cbHmacB64))
        return NTE_INVALID_PARAMETER;
    hmacB64[cbHmacB64] = 0;

    WCHAR headers[2048];
    swprintf_s(headers, 2048,
        L"Authorization: RKSP-HMAC-V2 %S\r\n"
        L"X-Timestamp: %S\r\n"
        L"X-Hash-Algorithm: %S\r\n"
        L"X-Client-Id: %S\r\n"
        L"X-Request-Nonce: %S\r\n"
        L"Content-Type: text/plain\r\n",
        hmacB64, timestamp, hashAlgorithm, clientId, nonce);

    HINTERNET hSession = WinHttpOpen(L"RemoteKSP/2.0",
        WINHTTP_ACCESS_TYPE_NO_PROXY, NULL, NULL, 0);
    if (!hSession) return NTE_INVALID_PARAMETER;

    HINTERNET hConnect = WinHttpConnect(hSession, host, uc.nPort, 0);
    if (!hConnect) {
        WinHttpCloseHandle(hSession);
        return NTE_INVALID_PARAMETER;
    }

    DWORD flags = uc.nScheme == INTERNET_SCHEME_HTTPS ? WINHTTP_FLAG_SECURE : 0;
    HINTERNET hRequest = WinHttpOpenRequest(hConnect,
        method[0] == 'G' ? L"GET" : L"POST",
        urlPath, NULL, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, flags);

    if (!hRequest) {
        WinHttpCloseHandle(hConnect);
        WinHttpCloseHandle(hSession);
        return NTE_INVALID_PARAMETER;
    }

    DWORD secFlags = SECURITY_FLAG_IGNORE_UNKNOWN_CA | SECURITY_FLAG_IGNORE_CERT_CN_INVALID |
                     SECURITY_FLAG_IGNORE_CERT_DATE_INVALID | SECURITY_FLAG_IGNORE_CERT_WRONG_USAGE;
    TlsPinContext pin = { tlsThumbprint, false, false };
    DWORD redirectPolicy = WINHTTP_OPTION_REDIRECT_POLICY_NEVER;
    if (!WinHttpSetOption(hRequest, WINHTTP_OPTION_SECURITY_FLAGS, &secFlags, sizeof(secFlags)) ||
        !WinHttpSetOption(hRequest, WINHTTP_OPTION_REDIRECT_POLICY, &redirectPolicy, sizeof(redirectPolicy)) ||
        WinHttpSetStatusCallback(hRequest, CheckTlsPin, WINHTTP_CALLBACK_FLAG_SEND_REQUEST, 0)
            == WINHTTP_INVALID_STATUS_CALLBACK) {
        WinHttpCloseHandle(hRequest);
        WinHttpCloseHandle(hConnect);
        WinHttpCloseHandle(hSession);
        return NTE_FAIL;
    }

    DWORD timeout = 30000;
    WinHttpSetOption(hRequest, WINHTTP_OPTION_CONNECT_TIMEOUT, &timeout, sizeof(timeout));
    WinHttpSetOption(hRequest, WINHTTP_OPTION_SEND_TIMEOUT, &timeout, sizeof(timeout));
    WinHttpSetOption(hRequest, WINHTTP_OPTION_RECEIVE_TIMEOUT, &timeout, sizeof(timeout));

    if (!WinHttpSendRequest(hRequest, headers, (DWORD)-1,
            (LPVOID)bodyBase64, (DWORD)strlen(bodyBase64), (DWORD)strlen(bodyBase64), (DWORD_PTR)&pin)
        || pin.rejected || !pin.checked) {
        if (!pin.rejected) WinHttpCloseHandle(hRequest);
        WinHttpCloseHandle(hConnect);
        WinHttpCloseHandle(hSession);
        return pin.rejected || !pin.checked ? NTE_PERM : NTE_INVALID_PARAMETER;
    }

    if (!WinHttpReceiveResponse(hRequest, NULL)) {
        WinHttpCloseHandle(hRequest);
        WinHttpCloseHandle(hConnect);
        WinHttpCloseHandle(hSession);
        return NTE_INVALID_PARAMETER;
    }

    // 非 200 时不把错误 JSON 当成签名/证书返回
    // 503 = 服务端测试模式、密钥源未就绪或证书因 PIN 停用，401 = 凭据无效
    DWORD statusCode = 0, statusSize = sizeof(statusCode);
    WinHttpQueryHeaders(hRequest, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
        NULL, &statusCode, &statusSize, NULL);
    if (statusCode != 200) {
        // 服务端证书因 PIN 停用：X-RemoteKSP-Error 头给出原因，映射成智能卡错误码，signtool 能显示具体原因
        WCHAR errorCode[32] = { 0 };
        DWORD errorSize = sizeof(errorCode);
        bool hasError = statusCode == 503 && WinHttpQueryHeaders(hRequest, WINHTTP_QUERY_CUSTOM,
            L"X-RemoteKSP-Error", errorCode, &errorSize, WINHTTP_NO_HEADER_INDEX);
        WinHttpCloseHandle(hRequest);
        WinHttpCloseHandle(hConnect);
        WinHttpCloseHandle(hSession);
        if (hasError && wcscmp(errorCode, L"pin-locked") == 0) return SCARD_W_CHV_BLOCKED;
        if (hasError && wcscmp(errorCode, L"pin-wrong") == 0) return SCARD_W_WRONG_CHV;
        if (statusCode == 503) return NTE_DEVICE_NOT_READY;
        if (statusCode == 401 || statusCode == 403) return NTE_PERM;
        return NTE_FAIL;
    }

    DWORD totalSize = 0, bufferSize = 4096;
    BYTE* buffer = (BYTE*)HeapAlloc(GetProcessHeap(), 0, bufferSize);

    while (true) {
        DWORD bytesRead = 0;
        if (!WinHttpReadData(hRequest, buffer + totalSize, bufferSize - totalSize, &bytesRead))
            break;
        if (bytesRead == 0)
            break;

        totalSize += bytesRead;
        if (totalSize + 1024 > bufferSize) {
            bufferSize *= 2;
            buffer = (BYTE*)HeapReAlloc(GetProcessHeap(), 0, buffer, bufferSize);
        }
    }

    WinHttpCloseHandle(hRequest);
    WinHttpCloseHandle(hConnect);
    WinHttpCloseHandle(hSession);

    *ppResponse = buffer;
    *pcbResponse = totalSize;
    return ERROR_SUCCESS;
}

// GET /cert/{thumbprint}，带 HMAC 认证头：服务端按客户端权限检查，未授权的证书拿不到
SECURITY_STATUS HttpGetCert(RemoteKspKey* key, BYTE** ppData, DWORD* pcbData)
{
    WCHAR path[256];
    swprintf_s(path, 256, L"/cert/%s", key->thumbprint);

    return HttpRequest(key->serverUrl, key->tlsThumbprint, key->clientId, key->secret,
        "GET", path, NULL, 0, "SHA256", ppData, pcbData);
}

SECURITY_STATUS HttpPostSign(RemoteKspKey* key, const WCHAR* hashAlg,
    const BYTE* hash, DWORD cbHash, BYTE** ppSig, DWORD* pcbSig)
{
    WCHAR path[256];
    swprintf_s(path, 256, L"/sign/%s", key->thumbprint);

    // 转换 hashAlg 为 ANSI
    char hashAlgA[32];
    WideCharToMultiByte(CP_UTF8, 0, hashAlg, -1, hashAlgA, 32, NULL, NULL);

    return HttpRequest(key->serverUrl, key->tlsThumbprint, key->clientId, key->secret,
        "POST", path, hash, cbHash, hashAlgA, ppSig, pcbSig);
}
