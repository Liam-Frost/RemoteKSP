#pragma once
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <bcrypt.h>
#include <ncrypt.h>
#include <winhttp.h>
#include <wincrypt.h>

// CNG status codes
#ifndef NTE_NOT_SUPPORTED
#define NTE_NOT_SUPPORTED 0x80090029
#endif
#ifndef NTE_BAD_FLAGS
#define NTE_BAD_FLAGS 0x80090009
#endif
#ifndef NTE_INVALID_PARAMETER
#define NTE_INVALID_PARAMETER 0x80090027
#endif
#ifndef NTE_NO_MORE_ITEMS
#define NTE_NO_MORE_ITEMS 0x8009002A
#endif
#ifndef NTE_BAD_SIGNATURE
#define NTE_BAD_SIGNATURE 0x80090006
#endif
#ifndef NTE_NOT_FOUND
#define NTE_NOT_FOUND 0x80090011
#endif
#ifndef NTE_BUFFER_TOO_SMALL
#define NTE_BUFFER_TOO_SMALL 0x80090028
#endif
#ifndef NTE_NO_MEMORY
#define NTE_NO_MEMORY 0x8009000E
#endif

// Function table from CNG Provider Development Kit
typedef SECURITY_STATUS (WINAPI *PFN_NCRYPT_OPEN_STORAGE_PROVIDER)(
    NCRYPT_PROV_HANDLE *phProvider, LPCWSTR pszProviderName, DWORD dwFlags);
typedef SECURITY_STATUS (WINAPI *PFN_NCRYPT_OPEN_KEY)(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE *phKey,
    LPCWSTR pszKeyName, DWORD dwLegacyKeySpec, DWORD dwFlags);
typedef SECURITY_STATUS (WINAPI *PFN_NCRYPT_CREATE_PERSISTED_KEY)(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE *phKey,
    LPCWSTR pszAlgId, LPCWSTR pszKeyName, DWORD dwLegacyKeySpec, DWORD dwFlags);
typedef SECURITY_STATUS (WINAPI *PFN_NCRYPT_GET_PROVIDER_PROPERTY)(
    NCRYPT_PROV_HANDLE hProvider, LPCWSTR pszProperty,
    PBYTE pbOutput, DWORD cbOutput, DWORD *pcbResult, DWORD dwFlags);
typedef SECURITY_STATUS (WINAPI *PFN_NCRYPT_GET_KEY_PROPERTY)(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hKey, LPCWSTR pszProperty,
    PBYTE pbOutput, DWORD cbOutput, DWORD *pcbResult, DWORD dwFlags);
typedef SECURITY_STATUS (WINAPI *PFN_NCRYPT_SET_PROVIDER_PROPERTY)(
    NCRYPT_PROV_HANDLE hProvider, LPCWSTR pszProperty,
    PBYTE pbInput, DWORD cbInput, DWORD dwFlags);
typedef SECURITY_STATUS (WINAPI *PFN_NCRYPT_SET_KEY_PROPERTY)(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hKey, LPCWSTR pszProperty,
    PBYTE pbInput, DWORD cbInput, DWORD dwFlags);
typedef SECURITY_STATUS (WINAPI *PFN_NCRYPT_FINALIZE_KEY)(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hKey, DWORD dwFlags);
typedef SECURITY_STATUS (WINAPI *PFN_NCRYPT_DELETE_KEY)(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hKey, DWORD dwFlags);
typedef SECURITY_STATUS (WINAPI *PFN_NCRYPT_FREE_PROVIDER)(
    NCRYPT_PROV_HANDLE hProvider);
typedef SECURITY_STATUS (WINAPI *PFN_NCRYPT_FREE_KEY)(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hKey);
typedef SECURITY_STATUS (WINAPI *PFN_NCRYPT_FREE_BUFFER)(
    PVOID pvInput);
typedef SECURITY_STATUS (WINAPI *PFN_NCRYPT_ENCRYPT)(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hKey, PBYTE pbInput, DWORD cbInput, VOID *pPaddingInfo,
    PBYTE pbOutput, DWORD cbOutput, DWORD *pcbResult, DWORD dwFlags);
typedef SECURITY_STATUS (WINAPI *PFN_NCRYPT_DECRYPT)(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hKey, PBYTE pbInput, DWORD cbInput, VOID *pPaddingInfo,
    PBYTE pbOutput, DWORD cbOutput, DWORD *pcbResult, DWORD dwFlags);
typedef SECURITY_STATUS (WINAPI *PFN_NCRYPT_IS_ALG_SUPPORTED)(
    NCRYPT_PROV_HANDLE hProvider, LPCWSTR pszAlgId, DWORD dwFlags);
typedef SECURITY_STATUS (WINAPI *PFN_NCRYPT_ENUM_ALGORITHMS)(
    NCRYPT_PROV_HANDLE hProvider, DWORD dwAlgOperations,
    DWORD *pdwAlgCount, NCryptAlgorithmName **ppAlgList, DWORD dwFlags);
typedef SECURITY_STATUS (WINAPI *PFN_NCRYPT_ENUM_KEYS)(
    NCRYPT_PROV_HANDLE hProvider, LPCWSTR pszScope,
    NCryptKeyName **ppKeyName, PVOID *ppEnumState, DWORD dwFlags);
typedef SECURITY_STATUS (WINAPI *PFN_NCRYPT_IMPORT_KEY)(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hImportKey,
    LPCWSTR pszBlobType, NCryptBufferDesc *pParameterList,
    NCRYPT_KEY_HANDLE *phKey, PBYTE pbData, DWORD cbData, DWORD dwFlags);
typedef SECURITY_STATUS (WINAPI *PFN_NCRYPT_EXPORT_KEY)(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hKey, NCRYPT_KEY_HANDLE hExportKey,
    LPCWSTR pszBlobType, NCryptBufferDesc *pParameterList,
    PBYTE pbOutput, DWORD cbOutput, DWORD *pcbResult, DWORD dwFlags);
typedef SECURITY_STATUS (WINAPI *PFN_NCRYPT_SIGN_HASH)(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hKey, VOID *pPaddingInfo,
    PBYTE pbHashValue, DWORD cbHashValue,
    PBYTE pbSignature, DWORD cbSignature, DWORD *pcbResult, DWORD dwFlags);
typedef SECURITY_STATUS (WINAPI *PFN_NCRYPT_VERIFY_SIGNATURE)(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hKey, VOID *pPaddingInfo,
    PBYTE pbHashValue, DWORD cbHashValue,
    PBYTE pbSignature, DWORD cbSignature, DWORD dwFlags);
typedef SECURITY_STATUS (WINAPI *PFN_NCRYPT_PROMPT_USER)(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hKey,
    LPCWSTR pszOperation, DWORD dwFlags);
typedef SECURITY_STATUS (WINAPI *PFN_NCRYPT_NOTIFY_CHANGE_KEY)(
    NCRYPT_PROV_HANDLE hProvider, HANDLE *phEvent, DWORD dwFlags);
typedef SECURITY_STATUS (WINAPI *PFN_NCRYPT_SECRET_AGREEMENT)(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_KEY_HANDLE hPrivKey, NCRYPT_KEY_HANDLE hPubKey,
    NCRYPT_SECRET_HANDLE *phAgreedSecret, DWORD dwFlags);
typedef SECURITY_STATUS (WINAPI *PFN_NCRYPT_DERIVE_KEY)(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_SECRET_HANDLE hSharedSecret, LPCWSTR pwszKDF,
    NCryptBufferDesc *pParameterList, PBYTE pbDerivedKey,
    DWORD cbDerivedKey, DWORD *pcbResult, ULONG dwFlags);
typedef SECURITY_STATUS (WINAPI *PFN_NCRYPT_FREE_SECRET)(
    NCRYPT_PROV_HANDLE hProvider, NCRYPT_SECRET_HANDLE hSharedSecret);

typedef struct _NCRYPT_KEY_STORAGE_FUNCTION_TABLE {
    BCRYPT_INTERFACE_VERSION Version;
    PFN_NCRYPT_OPEN_STORAGE_PROVIDER OpenStorageProvider;
    PFN_NCRYPT_OPEN_KEY OpenKey;
    PFN_NCRYPT_CREATE_PERSISTED_KEY CreatePersistedKey;
    PFN_NCRYPT_GET_PROVIDER_PROPERTY GetProviderProperty;
    PFN_NCRYPT_GET_KEY_PROPERTY GetKeyProperty;
    PFN_NCRYPT_SET_PROVIDER_PROPERTY SetProviderProperty;
    PFN_NCRYPT_SET_KEY_PROPERTY SetKeyProperty;
    PFN_NCRYPT_FINALIZE_KEY FinalizeKey;
    PFN_NCRYPT_DELETE_KEY DeleteKey;
    PFN_NCRYPT_FREE_PROVIDER FreeProvider;
    PFN_NCRYPT_FREE_KEY FreeKey;
    PFN_NCRYPT_FREE_BUFFER FreeBuffer;
    PFN_NCRYPT_ENCRYPT Encrypt;
    PFN_NCRYPT_DECRYPT Decrypt;
    PFN_NCRYPT_IS_ALG_SUPPORTED IsAlgSupported;
    PFN_NCRYPT_ENUM_ALGORITHMS EnumAlgorithms;
    PFN_NCRYPT_ENUM_KEYS EnumKeys;
    PFN_NCRYPT_IMPORT_KEY ImportKey;
    PFN_NCRYPT_EXPORT_KEY ExportKey;
    PFN_NCRYPT_SIGN_HASH SignHash;
    PFN_NCRYPT_VERIFY_SIGNATURE VerifySignature;
    PFN_NCRYPT_PROMPT_USER PromptUser;
    PFN_NCRYPT_NOTIFY_CHANGE_KEY NotifyChangeKey;
    PFN_NCRYPT_SECRET_AGREEMENT SecretAgreement;
    PFN_NCRYPT_DERIVE_KEY DeriveKey;
    PFN_NCRYPT_FREE_SECRET FreeSecret;
} NCRYPT_KEY_STORAGE_FUNCTION_TABLE;

#define REMOTEKSP_MAGIC_PROV 0x524B5350  // 'RKSP'
#define REMOTEKSP_MAGIC_KEY  0x524B4B59  // 'RKKY'

// 一个 provider 句柄可以打开来自不同服务端的密钥，所以服务端配置放在 key 上，
// 在 OpenKey 时按证书指纹从注册表映射表加载
struct RemoteKspProvider {
    DWORD magic;
};

struct RemoteKspKey {
    DWORD magic;
    RemoteKspProvider* prov;
    WCHAR thumbprint[128];
    WCHAR serverUrl[256];
    WCHAR tlsThumbprint[41];
    char secret[128];
    char clientId[64];
    BYTE* certDer;
    DWORD certDerLen;
    BCRYPT_KEY_HANDLE hPubKey;
    DWORD keyBits;
};

// KspEnumKeys 的枚举状态
struct RemoteKspEnumState {
    DWORD index;
};

#define REMOTEKSP_REG_ROOT  L"SOFTWARE\\RemoteKSP"
#define REMOTEKSP_REG_CERTS L"SOFTWARE\\RemoteKSP\\Certificates"
#define REMOTEKSP_REG_SERVERS L"SOFTWARE\\RemoteKSP\\Servers"

// http.cpp
SECURITY_STATUS HttpGetCert(RemoteKspKey* key, BYTE** ppData, DWORD* pcbData);
SECURITY_STATUS HttpPostSign(RemoteKspKey* key, const WCHAR* hashAlg,
    const BYTE* hash, DWORD cbHash, BYTE** ppSig, DWORD* pcbSig);

// Registry config (http.cpp)
// 证书指纹 → Certificates\{指纹}\ServerId → Servers\{ServerId}；没有 Certificates 键时按旧版单服务端读取根键
bool IsValidThumbprint(const WCHAR* thumbprint);
bool HasCertificateMap();
bool LoadKeyConfig(const WCHAR* thumbprint, WCHAR* serverUrl, size_t urlLen,
    char* secret, size_t secretLen, char* clientId, size_t clientIdLen,
    WCHAR* tlsThumbprint, size_t tlsLen);
