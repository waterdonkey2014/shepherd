/*
 * SHEPHERD-VITA: native entry points CoreLib references outside System.Native.
 * libhostpolicy (AssemblyDependencyResolver) does not exist without the dotnet host; these fail like the
 * real API does when no host is present, so managed code gets its documented "not supported" path.
 */
#include <stddef.h>
#include <stdint.h>

typedef void (*corehost_error_writer_fn) (const char *message);
typedef void (*corehost_resolve_component_dependencies_result_fn) (const char *assembly_paths,
	const char *native_search_paths, const char *resource_search_paths);

int corehost_resolve_component_dependencies (const char *component_main_assembly_path,
	corehost_resolve_component_dependencies_result_fn result)
{
	return (int) 0x80008083u; /* StatusCode::HostApiUnsupportedScenario */
}

corehost_error_writer_fn corehost_set_error_writer (corehost_error_writer_fn error_writer)
{
	return NULL;
}
