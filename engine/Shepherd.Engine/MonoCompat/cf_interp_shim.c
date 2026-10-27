// Shepherd — Mono-interpreter ABI shim for Rewired_OSX's CoreFoundation P/Invokes (Mac only; never built for the Vita).
//
// Rewired_OSX declares several CFIndex (64-bit `long`) parameters as Int32. Mono's interpreter calls simple native
// signatures through its fast path (transform.c interp_type_as_ptr: I4/bool/char/enum count as pointer-sized), which
// passes each argument as a full 64-bit stack slot without clearing the upper half. A callee that really takes a 32-bit
// int ignores those bits (AAPCS64), but CoreFoundation reads the whole CFIndex -> e.g. CFStringCreateWithCharacters
// gets a garbage length and memmove faults (SIGSEGV in __CFStringCreateImmutableFunnel3 during Rewired's HID init).
// The Mono JIT and CoreCLR write the argument to a w-register (upper half zeroed), so they never see this.
// These wrappers re-narrow the Int32-declared CFIndex arguments and forward to the real CoreFoundation; every other
// symbol the resolver looks up in this image comes from the re-exported CoreFoundation.
// Loaded only by MonoInterpCompat.cs, only under the Mono interpreter on a 64-bit process, only when SHEPHERD_MONO_CFSHIM
// points at the built dylib. On a 32-bit target (Vita) I4 is pointer-sized and the problem does not exist.
#include <dlfcn.h>
#include <stdint.h>
typedef long CFIndex;
typedef const void *CFTypeRef;
#define EXPORT __attribute__((visibility("default")))
#define NARROW(x) ((CFIndex)(int32_t)(x))

static void *real(const char *name)
{
    static void *cf;
    if (!cf) cf = dlopen("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", RTLD_LAZY | RTLD_LOCAL);
    return cf ? dlsym(cf, name) : 0;
}
#define REAL(self) static __typeof__(&self) f; if (!f) f = (__typeof__(f))real(#self)

// Rewired: IntPtr(IntPtr, String /*LPWStr*/, Int32)
EXPORT CFTypeRef CFStringCreateWithCharacters(CFTypeRef alloc, const uint16_t *chars, CFIndex numChars)
{ REAL(CFStringCreateWithCharacters); return f(alloc, chars, NARROW(numChars)); }

// Rewired: Char(IntPtr, Int32)
EXPORT uint16_t CFStringGetCharacterAtIndex(CFTypeRef str, CFIndex idx)
{ REAL(CFStringGetCharacterAtIndex); return f(str, NARROW(idx)); }

// Rewired: IntPtr(IntPtr, Int32 /*CFNumberType = CFIndex enum*/, IntPtr)
EXPORT CFTypeRef CFNumberCreate(CFTypeRef alloc, CFIndex type, const void *value)
{ REAL(CFNumberCreate); return f(alloc, NARROW(type), value); }

// Rewired: Boolean(IntPtr, Int32 /*CFNumberType*/, IntPtr)
EXPORT unsigned char CFNumberGetValue(CFTypeRef number, CFIndex type, void *value)
{ REAL(CFNumberGetValue); return f(number, NARROW(type), value); }

// Rewired: IntPtr(IntPtr, Int32 /*CFIndex order*/, IntPtr)
EXPORT CFTypeRef CFRunLoopSourceCreate(CFTypeRef alloc, CFIndex order, void *context)
{ REAL(CFRunLoopSourceCreate); return f(alloc, NARROW(order), context); }
