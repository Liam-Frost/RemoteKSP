#include "ksp.h"
#include <shlwapi.h>

// BCryptRegisterProvider etc. are not exported by standard SDK bcrypt.lib
// We dynamically load them from bcrypt.dll
typedef NTSTATUS (WINAPI *PFN_BCryptRegisterProvider)(
    LPCWSTR, ULONG, PCRYPT_PROVIDER_REG);
typedef NTSTATUS (WINAPI *PFN_BCryptUnregisterProvider)(LPCWSTR);
typedef NTSTATUS (WINAPI *PFN_BCryptAddContextFunctionProvider)(
    ULONG, LPCWSTR, ULONG, LPCWSTR, LPCWSTR, ULONG);
typedef NTSTATUS (WINAPI *PFN_BCryptRemoveContextFunctionProvider)(
    ULONG, LPCWSTR, ULONG, LPCWSTR, LPCWSTR);

#define REMOTEKSP_PROVIDER_NAME L"Remote KSP"

#ifndef CRYPT_OVERWRITE
#define CRYPT_OVERWRITE 0x00000001
#endif
#ifndef CRYPT_LOCAL
#define CRYPT_LOCAL 0x00000001
#endif

STDAPI DllRegisterServer(void)
{
    HMODULE hBCrypt = LoadLibraryW(L"bcrypt.dll");
    if (!hBCrypt) return E_FAIL;

    auto RegProv = (PFN_BCryptRegisterProvider)GetProcAddress(hBCrypt, "BCryptRegisterProvider");
    auto AddCtx = (PFN_BCryptAddContextFunctionProvider)GetProcAddress(hBCrypt, "BCryptAddContextFunctionProvider");
    if (!RegProv || !AddCtx) {
        FreeLibrary(hBCrypt);
        return E_FAIL;
    }

    PWSTR functions[] = { (PWSTR)NCRYPT_KEY_STORAGE_ALGORITHM };
    CRYPT_INTERFACE_REG iface = { NCRYPT_KEY_STORAGE_INTERFACE, CRYPT_LOCAL, 1, functions };
    PCRYPT_INTERFACE_REG ifaces[] = { &iface };
    CRYPT_IMAGE_REG image = { (PWSTR)L"RemoteKSP.dll", 1, ifaces };
    CRYPT_PROVIDER_REG reg = { 0, NULL, &image, NULL };

    NTSTATUS st = RegProv(REMOTEKSP_PROVIDER_NAME, CRYPT_OVERWRITE, &reg);
    if (st >= 0)
        st = AddCtx(CRYPT_LOCAL, NULL, NCRYPT_KEY_STORAGE_INTERFACE,
            NCRYPT_KEY_STORAGE_ALGORITHM, REMOTEKSP_PROVIDER_NAME, CRYPT_PRIORITY_BOTTOM);

    FreeLibrary(hBCrypt);
    return st >= 0 ? S_OK : E_FAIL;
}

STDAPI DllUnregisterServer(void)
{
    HMODULE hBCrypt = LoadLibraryW(L"bcrypt.dll");
    if (!hBCrypt) return S_OK;

    auto RemCtx = (PFN_BCryptRemoveContextFunctionProvider)GetProcAddress(hBCrypt, "BCryptRemoveContextFunctionProvider");
    auto UnregProv = (PFN_BCryptUnregisterProvider)GetProcAddress(hBCrypt, "BCryptUnregisterProvider");
    if (RemCtx) RemCtx(CRYPT_LOCAL, NULL, NCRYPT_KEY_STORAGE_INTERFACE, NCRYPT_KEY_STORAGE_ALGORITHM, REMOTEKSP_PROVIDER_NAME);
    if (UnregProv) UnregProv(REMOTEKSP_PROVIDER_NAME);

    FreeLibrary(hBCrypt);
    return S_OK;
}
