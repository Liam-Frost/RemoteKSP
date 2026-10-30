#include "../../native/ksp/ksp.h"
#include <stdio.h>
#include <stdlib.h>

// Synthetic credentials only. Exercises the production HTTP implementation without
// installing a provider, touching HKLM, or accessing a real signing certificate.
int wmain(int argc, WCHAR** argv)
{
    // 可选第 5 个参数：期望的 SECURITY_STATUS（十六进制），用于验证错误码映射
    if (argc != 4 && argc != 5) return 2;
    RemoteKspKey key = {};
    wcscpy_s(key.serverUrl, argv[1]);
    wcscpy_s(key.tlsThumbprint, argv[2]);
    wcscpy_s(key.thumbprint, L"0123456789012345678901234567890123456789");
    strcpy_s(key.clientId, "regression-client");
    strcpy_s(key.secret, "synthetic-regression-secret");
    BYTE* response = NULL;
    DWORD size = 0;
    SECURITY_STATUS status;
    if (wcscmp(argv[3], L"sign") == 0) {
        BYTE hash[32] = {};
        status = HttpPostSign(&key, L"SHA256", hash, sizeof(hash), &response, &size);
    } else {
        status = HttpGetCert(&key, &response, &size);
    }
    if (response) HeapFree(GetProcessHeap(), 0, response);
    printf("status=%08lx bytes=%lu\n", (unsigned long)status, size);
    if (argc == 5)
        return (unsigned long)status == wcstoul(argv[4], NULL, 16) ? 0 : 1;
    return status == ERROR_SUCCESS && size > 0 ? 0 : 1;
}
