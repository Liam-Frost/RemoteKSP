#include "ksp.h"
#include <stdio.h>

// Forward declarations of KSP functions
static SECURITY_STATUS WINAPI KspOpenProvider(
    NCRYPT_PROV_HANDLE* phProvider, LPCWSTR pszProviderName, DWORD dwFlags);
static SECURITY_STATUS WINAPI KspOpenKey(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE* phKey,
    LPCWSTR pszKeyName, DWORD dwLegacyKeySpec, DWORD dwFlags);
static SECURITY_STATUS WINAPI KspCreatePersistedKey(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE* phKey,
    LPCWSTR pszAlgId, LPCWSTR pszKeyName, DWORD dwLegacyKeySpec, DWORD dwFlags);
static SECURITY_STATUS WINAPI KspGetProviderProperty(
    NCRYPT_PROV_HANDLE hProvider, LPCWSTR pszProperty,
    PBYTE pbOutput, DWORD cbOutput, DWORD* pcbResult, DWORD dwFlags);
static SECURITY_STATUS WINAPI KspGetKeyProperty(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hKey, LPCWSTR pszProperty,
    PBYTE pbOutput, DWORD cbOutput, DWORD* pcbResult, DWORD dwFlags);
static SECURITY_STATUS WINAPI KspSetProviderProperty(
    NCRYPT_PROV_HANDLE hProvider, LPCWSTR pszProperty,
    PBYTE pbInput, DWORD cbInput, DWORD dwFlags);
static SECURITY_STATUS WINAPI KspSetKeyProperty(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hKey, LPCWSTR pszProperty,
    PBYTE pbInput, DWORD cbInput, DWORD dwFlags);
static SECURITY_STATUS WINAPI KspFinalizeKey(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hKey, DWORD dwFlags);
static SECURITY_STATUS WINAPI KspDeleteKey(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hKey, DWORD dwFlags);
static SECURITY_STATUS WINAPI KspFreeProvider(NCRYPT_PROV_HANDLE hProvider);
static SECURITY_STATUS WINAPI KspFreeKey(NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hKey);
static SECURITY_STATUS WINAPI KspFreeBuffer(PVOID pvInput);
static SECURITY_STATUS WINAPI KspEncrypt(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hKey, PBYTE pbInput, DWORD cbInput, VOID* pPaddingInfo,
    PBYTE pbOutput, DWORD cbOutput, DWORD* pcbResult, DWORD dwFlags);
static SECURITY_STATUS WINAPI KspDecrypt(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hKey, PBYTE pbInput, DWORD cbInput, VOID* pPaddingInfo,
    PBYTE pbOutput, DWORD cbOutput, DWORD* pcbResult, DWORD dwFlags);
static SECURITY_STATUS WINAPI KspIsAlgSupported(
    NCRYPT_PROV_HANDLE hProvider, LPCWSTR pszAlgId, DWORD dwFlags);
static SECURITY_STATUS WINAPI KspEnumAlgorithms(
    NCRYPT_PROV_HANDLE hProvider, DWORD dwAlgOperations,
    DWORD* pdwAlgCount, NCryptAlgorithmName** ppAlgList, DWORD dwFlags);
static SECURITY_STATUS WINAPI KspEnumKeys(
    NCRYPT_PROV_HANDLE hProvider, LPCWSTR pszScope,
    NCryptKeyName** ppKeyName, PVOID* ppEnumState, DWORD dwFlags);
static SECURITY_STATUS WINAPI KspImportKey(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hImportKey,
    LPCWSTR pszBlobType, NCryptBufferDesc* pParameterList,
    NCRYPT_KEY_HANDLE* phKey, PBYTE pbData, DWORD cbData, DWORD dwFlags);
static SECURITY_STATUS WINAPI KspExportKey(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hKey, NCRYPT_KEY_HANDLE hExportKey,
    LPCWSTR pszBlobType, NCryptBufferDesc* pParameterList,
    PBYTE pbOutput, DWORD cbOutput, DWORD* pcbResult, DWORD dwFlags);
static SECURITY_STATUS WINAPI KspSignHash(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hKey, VOID* pPaddingInfo,
    PBYTE pbHashValue, DWORD cbHashValue,
    PBYTE pbSignature, DWORD cbSignature, DWORD* pcbResult, DWORD dwFlags);
static SECURITY_STATUS WINAPI KspVerifySignature(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hKey, VOID* pPaddingInfo,
    PBYTE pbHashValue, DWORD cbHashValue,
    PBYTE pbSignature, DWORD cbSignature, DWORD dwFlags);
static SECURITY_STATUS WINAPI KspPromptUser(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hKey,
    LPCWSTR pszOperation, DWORD dwFlags);
static SECURITY_STATUS WINAPI KspNotifyChangeKey(
    NCRYPT_PROV_HANDLE hProvider, HANDLE* phEvent, DWORD dwFlags);
static SECURITY_STATUS WINAPI KspSecretAgreement(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hPrivKey, NCRYPT_KEY_HANDLE hPubKey,
    NCRYPT_SECRET_HANDLE* phAgreedSecret, DWORD dwFlags);
static SECURITY_STATUS WINAPI KspDeriveKey(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_SECRET_HANDLE hSharedSecret, LPCWSTR pwszKDF,
    NCryptBufferDesc* pParameterList, PBYTE pbDerivedKey,
    DWORD cbDerivedKey, DWORD* pcbResult, ULONG dwFlags);
static SECURITY_STATUS WINAPI KspFreeSecret(NCRYPT_PROV_HANDLE hProvider, NCRYPT_SECRET_HANDLE hSharedSecret);

static NCRYPT_KEY_STORAGE_FUNCTION_TABLE g_FunctionTable = {
    { 1, 0 },
    KspOpenProvider,
    KspOpenKey,
    KspCreatePersistedKey,
    KspGetProviderProperty,
    KspGetKeyProperty,
    KspSetProviderProperty,
    KspSetKeyProperty,
    KspFinalizeKey,
    KspDeleteKey,
    KspFreeProvider,
    KspFreeKey,
    KspFreeBuffer,
    KspEncrypt,
    KspDecrypt,
    KspIsAlgSupported,
    KspEnumAlgorithms,
    KspEnumKeys,
    KspImportKey,
    KspExportKey,
    KspSignHash,
    KspVerifySignature,
    KspPromptUser,
    KspNotifyChangeKey,
    KspSecretAgreement,
    KspDeriveKey,
    KspFreeSecret
};

extern "C" __declspec(dllexport)
SECURITY_STATUS WINAPI GetKeyStorageInterface(
    LPCWSTR pszProviderName,
    NCRYPT_KEY_STORAGE_FUNCTION_TABLE** ppFunctionTable,
    DWORD dwFlags)
{
    if (ppFunctionTable == NULL)
        return NTE_INVALID_PARAMETER;
    *ppFunctionTable = &g_FunctionTable;
    return ERROR_SUCCESS;
}

static SECURITY_STATUS WINAPI KspOpenProvider(
    NCRYPT_PROV_HANDLE* phProvider, LPCWSTR pszProviderName, DWORD dwFlags)
{
    if (phProvider == NULL)
        return NTE_INVALID_PARAMETER;

    RemoteKspProvider* prov = (RemoteKspProvider*)HeapAlloc(
        GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(RemoteKspProvider));
    if (!prov)
        return NTE_NO_MEMORY;

    // 服务端配置按证书在 OpenKey 时加载；未配对时 provider 也能打开（枚举结果为空）
    prov->magic = REMOTEKSP_MAGIC_PROV;

    *phProvider = (NCRYPT_PROV_HANDLE)prov;
    return ERROR_SUCCESS;
}

static SECURITY_STATUS WINAPI KspOpenKey(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE* phKey,
    LPCWSTR pszKeyName, DWORD dwLegacyKeySpec, DWORD dwFlags)
{
    RemoteKspProvider* prov = (RemoteKspProvider*)hProvider;
    if (!prov || prov->magic != REMOTEKSP_MAGIC_PROV || !phKey || !pszKeyName)
        return NTE_INVALID_PARAMETER;

    RemoteKspKey* key = (RemoteKspKey*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(RemoteKspKey));
    if (!key)
        return NTE_NO_MEMORY;

    key->magic = REMOTEKSP_MAGIC_KEY;
    key->prov = prov;

    // 容器名就是证书指纹；按映射表找到它所属的服务端
    if (!IsValidThumbprint(pszKeyName) ||
        wcscpy_s(key->thumbprint, 128, pszKeyName) != 0 ||
        !LoadKeyConfig(key->thumbprint, key->serverUrl, 256, key->secret, 128, key->clientId, 64,
            key->tlsThumbprint, 41)) {
        SecureZeroMemory(key, sizeof(RemoteKspKey));
        HeapFree(GetProcessHeap(), 0, key);
        return NTE_BAD_KEYSET;
    }

    BYTE* certDer = NULL;
    DWORD certDerLen = 0;
    SECURITY_STATUS status = HttpGetCert(key, &certDer, &certDerLen);
    if (status != ERROR_SUCCESS) {
        SecureZeroMemory(key, sizeof(RemoteKspKey));
        HeapFree(GetProcessHeap(), 0, key);
        return status;
    }

    PCCERT_CONTEXT pCert = CertCreateCertificateContext(X509_ASN_ENCODING, certDer, certDerLen);
    if (!pCert) {
        HeapFree(GetProcessHeap(), 0, certDer);
        SecureZeroMemory(key, sizeof(RemoteKspKey));
        HeapFree(GetProcessHeap(), 0, key);
        return NTE_INVALID_PARAMETER;
    }

    CERT_PUBLIC_KEY_INFO* pki = &pCert->pCertInfo->SubjectPublicKeyInfo;
    BCRYPT_KEY_HANDLE hPubKey = NULL;
    if (!CryptImportPublicKeyInfoEx2(X509_ASN_ENCODING, pki, 0, NULL, &hPubKey)) {
        CertFreeCertificateContext(pCert);
        HeapFree(GetProcessHeap(), 0, certDer);
        SecureZeroMemory(key, sizeof(RemoteKspKey));
        HeapFree(GetProcessHeap(), 0, key);
        return NTE_INVALID_PARAMETER;
    }

    DWORD keyBits = 0, cbResult = 0;
    BCryptGetProperty(hPubKey, BCRYPT_KEY_STRENGTH, (PBYTE)&keyBits, sizeof(keyBits), &cbResult, 0);

    CertFreeCertificateContext(pCert);

    key->certDer = certDer;
    key->certDerLen = certDerLen;
    key->hPubKey = hPubKey;
    key->keyBits = keyBits;

    *phKey = (NCRYPT_KEY_HANDLE)key;
    return ERROR_SUCCESS;
}

static SECURITY_STATUS WINAPI KspSignHash(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hKey, VOID* pPaddingInfo,
    PBYTE pbHashValue, DWORD cbHashValue,
    PBYTE pbSignature, DWORD cbSignature, DWORD* pcbResult, DWORD dwFlags)
{
    RemoteKspKey* key = (RemoteKspKey*)hKey;
    if (!key || key->magic != REMOTEKSP_MAGIC_KEY || !pbHashValue || !pcbResult)
        return NTE_INVALID_PARAMETER;

    // Only PKCS1 padding supported (Authenticode uses this)
    if (!(dwFlags & BCRYPT_PAD_PKCS1))
        return NTE_NOT_SUPPORTED;

    DWORD sigSize = key->keyBits / 8;
    if (!pbSignature) {
        *pcbResult = sigSize;
        return ERROR_SUCCESS;
    }
    if (cbSignature < sigSize)
        return NTE_BUFFER_TOO_SMALL;

    BCRYPT_PKCS1_PADDING_INFO* pkcs1 = (BCRYPT_PKCS1_PADDING_INFO*)pPaddingInfo;
    if (!pkcs1 || !pkcs1->pszAlgId)
        return NTE_NOT_SUPPORTED;

    BYTE* sig = NULL;
    DWORD cbSig = 0;
    SECURITY_STATUS status = HttpPostSign(key, pkcs1->pszAlgId, pbHashValue, cbHashValue, &sig, &cbSig);

    if (status != ERROR_SUCCESS)
        return status;

    if (cbSig > cbSignature) {
        HeapFree(GetProcessHeap(), 0, sig);
        return NTE_BUFFER_TOO_SMALL;
    }

    // Verify signature locally before returning
    if (!BCRYPT_SUCCESS(BCryptVerifySignature(key->hPubKey, pkcs1, pbHashValue,
            cbHashValue, sig, cbSig, BCRYPT_PAD_PKCS1))) {
        HeapFree(GetProcessHeap(), 0, sig);
        return NTE_BAD_SIGNATURE;
    }

    memcpy(pbSignature, sig, cbSig);
    *pcbResult = cbSig;
    HeapFree(GetProcessHeap(), 0, sig);
    return ERROR_SUCCESS;
}

static SECURITY_STATUS WINAPI KspGetKeyProperty(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hKey, LPCWSTR pszProperty,
    PBYTE pbOutput, DWORD cbOutput, DWORD* pcbResult, DWORD dwFlags)
{
    RemoteKspKey* key = (RemoteKspKey*)hKey;
    if (!key || key->magic != REMOTEKSP_MAGIC_KEY || !pszProperty || !pcbResult)
        return NTE_INVALID_PARAMETER;

    if (wcscmp(pszProperty, NCRYPT_ALGORITHM_GROUP_PROPERTY) == 0) {
        const WCHAR* val = NCRYPT_RSA_ALGORITHM_GROUP;
        DWORD cb = (DWORD)((wcslen(val) + 1) * sizeof(WCHAR));
        *pcbResult = cb;
        if (!pbOutput) return ERROR_SUCCESS;
        if (cbOutput < cb) return NTE_BUFFER_TOO_SMALL;
        memcpy(pbOutput, val, cb);
        return ERROR_SUCCESS;
    }
    if (wcscmp(pszProperty, NCRYPT_ALGORITHM_PROPERTY) == 0) {
        const WCHAR* val = BCRYPT_RSA_ALGORITHM;
        DWORD cb = (DWORD)((wcslen(val) + 1) * sizeof(WCHAR));
        *pcbResult = cb;
        if (!pbOutput) return ERROR_SUCCESS;
        if (cbOutput < cb) return NTE_BUFFER_TOO_SMALL;
        memcpy(pbOutput, val, cb);
        return ERROR_SUCCESS;
    }
    if (wcscmp(pszProperty, NCRYPT_LENGTH_PROPERTY) == 0) {
        *pcbResult = sizeof(DWORD);
        if (!pbOutput) return ERROR_SUCCESS;
        if (cbOutput < sizeof(DWORD)) return NTE_BUFFER_TOO_SMALL;
        *(DWORD*)pbOutput = key->keyBits;
        return ERROR_SUCCESS;
    }
    if (wcscmp(pszProperty, NCRYPT_KEY_USAGE_PROPERTY) == 0) {
        DWORD usage = NCRYPT_ALLOW_SIGNING_FLAG;
        *pcbResult = sizeof(DWORD);
        if (!pbOutput) return ERROR_SUCCESS;
        if (cbOutput < sizeof(DWORD)) return NTE_BUFFER_TOO_SMALL;
        *(DWORD*)pbOutput = usage;
        return ERROR_SUCCESS;
    }
    if (wcscmp(pszProperty, NCRYPT_EXPORT_POLICY_PROPERTY) == 0) {
        DWORD policy = 0;
        *pcbResult = sizeof(DWORD);
        if (!pbOutput) return ERROR_SUCCESS;
        if (cbOutput < sizeof(DWORD)) return NTE_BUFFER_TOO_SMALL;
        *(DWORD*)pbOutput = policy;
        return ERROR_SUCCESS;
    }
    if (wcscmp(pszProperty, NCRYPT_NAME_PROPERTY) == 0 ||
        wcscmp(pszProperty, NCRYPT_UNIQUE_NAME_PROPERTY) == 0) {
        DWORD cb = (DWORD)((wcslen(key->thumbprint) + 1) * sizeof(WCHAR));
        *pcbResult = cb;
        if (!pbOutput) return ERROR_SUCCESS;
        if (cbOutput < cb) return NTE_BUFFER_TOO_SMALL;
        memcpy(pbOutput, key->thumbprint, cb);
        return ERROR_SUCCESS;
    }
    if (wcscmp(pszProperty, NCRYPT_CERTIFICATE_PROPERTY) == 0) {
        *pcbResult = key->certDerLen;
        if (!pbOutput) return ERROR_SUCCESS;
        if (cbOutput < key->certDerLen) return NTE_BUFFER_TOO_SMALL;
        memcpy(pbOutput, key->certDer, key->certDerLen);
        return ERROR_SUCCESS;
    }

    return NTE_NOT_SUPPORTED;
}

static SECURITY_STATUS WINAPI KspExportKey(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hKey, NCRYPT_KEY_HANDLE hExportKey,
    LPCWSTR pszBlobType, NCryptBufferDesc* pParameterList,
    PBYTE pbOutput, DWORD cbOutput, DWORD* pcbResult, DWORD dwFlags)
{
    RemoteKspKey* key = (RemoteKspKey*)hKey;
    if (!key || key->magic != REMOTEKSP_MAGIC_KEY || !pszBlobType || !pcbResult)
        return NTE_INVALID_PARAMETER;

    if (wcscmp(pszBlobType, BCRYPT_RSAPUBLIC_BLOB) != 0 &&
        wcscmp(pszBlobType, LEGACY_RSAPUBLIC_BLOB) != 0)
        return NTE_NOT_SUPPORTED;

    return BCryptExportKey(key->hPubKey, NULL, pszBlobType, pbOutput, cbOutput, pcbResult, 0);
}

static SECURITY_STATUS WINAPI KspVerifySignature(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hKey, VOID* pPaddingInfo,
    PBYTE pbHashValue, DWORD cbHashValue,
    PBYTE pbSignature, DWORD cbSignature, DWORD dwFlags)
{
    RemoteKspKey* key = (RemoteKspKey*)hKey;
    if (!key || key->magic != REMOTEKSP_MAGIC_KEY || !pbHashValue || !pbSignature)
        return NTE_INVALID_PARAMETER;

    // Respect padding flags
    DWORD bcryptFlags = (dwFlags & BCRYPT_PAD_PKCS1) ? BCRYPT_PAD_PKCS1 : 0;
    if (dwFlags & BCRYPT_PAD_PSS)
        return NTE_NOT_SUPPORTED;

    BCRYPT_PKCS1_PADDING_INFO* pkcs1 = (BCRYPT_PKCS1_PADDING_INFO*)pPaddingInfo;
    if (!pkcs1 || !pkcs1->pszAlgId)
        return NTE_NOT_SUPPORTED;

    NTSTATUS status = BCryptVerifySignature(key->hPubKey, pkcs1, pbHashValue,
        cbHashValue, pbSignature, cbSignature, bcryptFlags);

    return status == 0 ? ERROR_SUCCESS : NTE_BAD_SIGNATURE;
}

static SECURITY_STATUS WINAPI KspEnumKeys(
    NCRYPT_PROV_HANDLE hProvider, LPCWSTR pszScope,
    NCryptKeyName** ppKeyName, PVOID* ppEnumState, DWORD dwFlags)
{
    RemoteKspProvider* prov = (RemoteKspProvider*)hProvider;
    if (!prov || prov->magic != REMOTEKSP_MAGIC_PROV || !ppKeyName || !ppEnumState)
        return NTE_INVALID_PARAMETER;

    RemoteKspEnumState* state = (RemoteKspEnumState*)*ppEnumState;
    if (!state) {
        state = (RemoteKspEnumState*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(RemoteKspEnumState));
        if (!state)
            return NTE_NO_MEMORY;
        *ppEnumState = state;
    }

    WCHAR thumbprint[128] = {0};
    if (HasCertificateMap()) {
        // 多服务端：Certificates 下每个子键名是一张证书的指纹，跳过不合法的条目
        HKEY hKey;
        if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, REMOTEKSP_REG_CERTS, 0,
            KEY_READ | KEY_WOW64_64KEY, &hKey) != ERROR_SUCCESS)
            return NTE_NO_MORE_ITEMS;

        LONG result;
        do {
            DWORD nameLen = 128;
            result = RegEnumKeyExW(hKey, state->index++, thumbprint, &nameLen, NULL, NULL, NULL, NULL);
        } while (result == ERROR_SUCCESS && !IsValidThumbprint(thumbprint));
        RegCloseKey(hKey);

        if (result != ERROR_SUCCESS)
            return NTE_NO_MORE_ITEMS;
    } else {
        // 旧版：根键下只有一个 Thumbprint 值
        if (state->index > 0)
            return NTE_NO_MORE_ITEMS;
        state->index++;

        HKEY hKey;
        if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, REMOTEKSP_REG_ROOT, 0,
            KEY_READ | KEY_WOW64_64KEY, &hKey) != ERROR_SUCCESS)
            return NTE_NO_MORE_ITEMS;

        DWORD size = sizeof(thumbprint) - sizeof(WCHAR);
        LONG result = RegQueryValueExW(hKey, L"Thumbprint", NULL, NULL, (BYTE*)thumbprint, &size);
        RegCloseKey(hKey);

        if (result != ERROR_SUCCESS || !IsValidThumbprint(thumbprint))
            return NTE_NO_MORE_ITEMS;
    }

    size_t nameLen = wcslen(thumbprint) + 1;
    size_t algLen = wcslen(BCRYPT_RSA_ALGORITHM) + 1;
    size_t totalSize = sizeof(NCryptKeyName) + (nameLen + algLen) * sizeof(WCHAR);

    NCryptKeyName* pKeyName = (NCryptKeyName*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, totalSize);
    if (!pKeyName)
        return NTE_NO_MEMORY;

    WCHAR* pNameBuf = (WCHAR*)(pKeyName + 1);
    WCHAR* pAlgBuf = pNameBuf + nameLen;

    wcscpy_s(pNameBuf, nameLen, thumbprint);
    wcscpy_s(pAlgBuf, algLen, BCRYPT_RSA_ALGORITHM);

    pKeyName->pszName = pNameBuf;
    pKeyName->pszAlgid = pAlgBuf;
    pKeyName->dwLegacyKeySpec = 0;
    pKeyName->dwFlags = 0;

    *ppKeyName = pKeyName;
    return ERROR_SUCCESS;
}

static SECURITY_STATUS WINAPI KspGetProviderProperty(
    NCRYPT_PROV_HANDLE hProvider, LPCWSTR pszProperty,
    PBYTE pbOutput, DWORD cbOutput, DWORD* pcbResult, DWORD dwFlags)
{
    RemoteKspProvider* prov = (RemoteKspProvider*)hProvider;
    if (!prov || prov->magic != REMOTEKSP_MAGIC_PROV || !pszProperty || !pcbResult)
        return NTE_INVALID_PARAMETER;

    if (wcscmp(pszProperty, NCRYPT_NAME_PROPERTY) == 0) {
        const WCHAR* name = L"Remote KSP";
        DWORD cb = (DWORD)((wcslen(name) + 1) * sizeof(WCHAR));
        *pcbResult = cb;
        if (!pbOutput) return ERROR_SUCCESS;
        if (cbOutput < cb) return NTE_BUFFER_TOO_SMALL;
        memcpy(pbOutput, name, cb);
        return ERROR_SUCCESS;
    }

    if (wcscmp(pszProperty, NCRYPT_IMPL_TYPE_PROPERTY) == 0) {
        DWORD impl = NCRYPT_IMPL_HARDWARE_FLAG;
        *pcbResult = sizeof(DWORD);
        if (!pbOutput) return ERROR_SUCCESS;
        if (cbOutput < sizeof(DWORD)) return NTE_BUFFER_TOO_SMALL;
        *(DWORD*)pbOutput = impl;
        return ERROR_SUCCESS;
    }

    return NTE_NOT_SUPPORTED;
}

static SECURITY_STATUS WINAPI KspIsAlgSupported(
    NCRYPT_PROV_HANDLE hProvider, LPCWSTR pszAlgId, DWORD dwFlags)
{
    if (!pszAlgId)
        return NTE_INVALID_PARAMETER;
    return wcscmp(pszAlgId, BCRYPT_RSA_ALGORITHM) == 0 ? ERROR_SUCCESS : NTE_NOT_SUPPORTED;
}

static SECURITY_STATUS WINAPI KspEnumAlgorithms(
    NCRYPT_PROV_HANDLE hProvider, DWORD dwAlgOperations,
    DWORD* pdwAlgCount, NCryptAlgorithmName** ppAlgList, DWORD dwFlags)
{
    if (!pdwAlgCount || !ppAlgList)
        return NTE_INVALID_PARAMETER;

    size_t algLen = wcslen(BCRYPT_RSA_ALGORITHM) + 1;
    size_t totalSize = sizeof(NCryptAlgorithmName) + algLen * sizeof(WCHAR);

    NCryptAlgorithmName* pAlg = (NCryptAlgorithmName*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, totalSize);
    if (!pAlg)
        return NTE_NO_MEMORY;

    WCHAR* pAlgBuf = (WCHAR*)(pAlg + 1);
    wcscpy_s(pAlgBuf, algLen, BCRYPT_RSA_ALGORITHM);

    pAlg->pszName = pAlgBuf;
    pAlg->dwClass = NCRYPT_ASYMMETRIC_ENCRYPTION_INTERFACE;
    pAlg->dwAlgOperations = NCRYPT_SIGNATURE_OPERATION;
    pAlg->dwFlags = 0;

    *ppAlgList = pAlg;
    *pdwAlgCount = 1;
    return ERROR_SUCCESS;
}

static SECURITY_STATUS WINAPI KspFreeProvider(NCRYPT_PROV_HANDLE hProvider)
{
    RemoteKspProvider* prov = (RemoteKspProvider*)hProvider;
    if (prov && prov->magic == REMOTEKSP_MAGIC_PROV) {
        HeapFree(GetProcessHeap(), 0, prov);
    }
    return ERROR_SUCCESS;
}

static SECURITY_STATUS WINAPI KspFreeKey(NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hKey)
{
    RemoteKspKey* key = (RemoteKspKey*)hKey;
    if (key && key->magic == REMOTEKSP_MAGIC_KEY) {
        if (key->hPubKey) BCryptDestroyKey(key->hPubKey);
        if (key->certDer) HeapFree(GetProcessHeap(), 0, key->certDer);
        SecureZeroMemory(key, sizeof(RemoteKspKey)); // 清掉内存里的 HMAC secret
        HeapFree(GetProcessHeap(), 0, key);
    }
    return ERROR_SUCCESS;
}

static SECURITY_STATUS WINAPI KspFreeBuffer(PVOID pvInput)
{
    if (pvInput) HeapFree(GetProcessHeap(), 0, pvInput);
    return ERROR_SUCCESS;
}

static SECURITY_STATUS WINAPI KspSetProviderProperty(
    NCRYPT_PROV_HANDLE hProvider, LPCWSTR pszProperty, PBYTE pbInput, DWORD cbInput, DWORD dwFlags)
{
    return ERROR_SUCCESS;
}

static SECURITY_STATUS WINAPI KspSetKeyProperty(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hKey, LPCWSTR pszProperty, PBYTE pbInput, DWORD cbInput, DWORD dwFlags)
{
    return ERROR_SUCCESS;
}

static SECURITY_STATUS WINAPI KspCreatePersistedKey(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE* phKey,
    LPCWSTR pszAlgId, LPCWSTR pszKeyName, DWORD dwLegacyKeySpec, DWORD dwFlags)
{
    return NTE_NOT_SUPPORTED;
}

static SECURITY_STATUS WINAPI KspFinalizeKey(NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hKey, DWORD dwFlags)
{
    return NTE_NOT_SUPPORTED;
}

static SECURITY_STATUS WINAPI KspDeleteKey(NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hKey, DWORD dwFlags)
{
    return NTE_NOT_SUPPORTED;
}

static SECURITY_STATUS WINAPI KspEncrypt(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hKey, PBYTE pbInput, DWORD cbInput, VOID* pPaddingInfo,
    PBYTE pbOutput, DWORD cbOutput, DWORD* pcbResult, DWORD dwFlags)
{
    return NTE_NOT_SUPPORTED;
}

static SECURITY_STATUS WINAPI KspDecrypt(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hKey, PBYTE pbInput, DWORD cbInput, VOID* pPaddingInfo,
    PBYTE pbOutput, DWORD cbOutput, DWORD* pcbResult, DWORD dwFlags)
{
    return NTE_NOT_SUPPORTED;
}

static SECURITY_STATUS WINAPI KspImportKey(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hImportKey,
    LPCWSTR pszBlobType, NCryptBufferDesc* pParameterList,
    NCRYPT_KEY_HANDLE* phKey, PBYTE pbData, DWORD cbData, DWORD dwFlags)
{
    return NTE_NOT_SUPPORTED;
}

static SECURITY_STATUS WINAPI KspPromptUser(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hKey, LPCWSTR pszOperation, DWORD dwFlags)
{
    return NTE_NOT_SUPPORTED;
}

static SECURITY_STATUS WINAPI KspNotifyChangeKey(
    NCRYPT_PROV_HANDLE hProvider, HANDLE* phEvent, DWORD dwFlags)
{
    return NTE_NOT_SUPPORTED;
}

static SECURITY_STATUS WINAPI KspSecretAgreement(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hPrivKey, NCRYPT_KEY_HANDLE hPubKey,
    NCRYPT_SECRET_HANDLE* phAgreedSecret, DWORD dwFlags)
{
    return NTE_NOT_SUPPORTED;
}

static SECURITY_STATUS WINAPI KspDeriveKey(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_SECRET_HANDLE hSharedSecret, LPCWSTR pwszKDF,
    NCryptBufferDesc* pParameterList, PBYTE pbDerivedKey,
    DWORD cbDerivedKey, DWORD* pcbResult, ULONG dwFlags)
{
    return NTE_NOT_SUPPORTED;
}

static SECURITY_STATUS WINAPI KspFreeSecret(NCRYPT_PROV_HANDLE hProvider, NCRYPT_SECRET_HANDLE hSharedSecret)
{
    return NTE_NOT_SUPPORTED;
}
