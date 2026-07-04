#ifndef FSHARP_POLYCALL_H
#define FSHARP_POLYCALL_H

#if defined(_WIN32) && defined(FSHARP_POLYCALL_BUILD_SHARED)
#define FSHARP_POLYCALL_API __declspec(dllexport)
#elif defined(__GNUC__) && defined(FSHARP_POLYCALL_BUILD_SHARED)
#define FSHARP_POLYCALL_API __attribute__((visibility("default")))
#else
#define FSHARP_POLYCALL_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

FSHARP_POLYCALL_API int fsharp_polycall_run_config(const char *config_path);

#ifdef __cplusplus
}
#endif

#endif
