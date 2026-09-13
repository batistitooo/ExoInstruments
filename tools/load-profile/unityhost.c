// Runs a managed exe on KSP's own runtime: Unity's Mono 5.11 with the Boehm GC, loading KSP's mscorlib.
// Built and invoked by unity.sh.
#include <stdio.h>
#include <stdlib.h>

typedef struct _MonoDomain MonoDomain;
typedef struct _MonoAssembly MonoAssembly;
extern void mono_set_dirs(const char *assembly_dir, const char *config_dir);
extern void mono_set_assemblies_path(const char *path);
extern void mono_config_parse(const char *filename);
extern MonoDomain *mono_jit_init_version(const char *name, const char *version);
extern MonoAssembly *mono_domain_assembly_open(MonoDomain *domain, const char *name);
extern int mono_jit_exec(MonoDomain *domain, MonoAssembly *assembly, int argc, char *argv[]);
extern char *mono_get_runtime_build_info(void);

int main(int argc, char *argv[])
{
    const char *managed = getenv("UNITY_MANAGED"), *etc = getenv("UNITY_ETC");
    if (argc < 2 || !managed || !etc)
    {
        fprintf(stderr, "usage: UNITY_MANAGED=<Managed dir> UNITY_ETC=<etc dir> unityhost <exe> [args]\n");
        return 2;
    }
    mono_set_dirs(managed, etc);
    mono_set_assemblies_path(managed);
    mono_config_parse(NULL);
    fprintf(stderr, "unity mono %s\n", mono_get_runtime_build_info());

    MonoDomain *domain = mono_jit_init_version("unityhost", "v4.0.30319");
    MonoAssembly *assembly = mono_domain_assembly_open(domain, argv[1]);
    if (!assembly)
    {
        fprintf(stderr, "cannot open %s\n", argv[1]);
        return 3;
    }
    int result = mono_jit_exec(domain, assembly, argc - 1, argv + 1);
    fflush(stdout);
    return result;
}
