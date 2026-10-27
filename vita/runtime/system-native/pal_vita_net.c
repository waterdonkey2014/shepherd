/*
 * SHEPHERD-VITA: the networking part of libSystem.Native for the PS Vita: the Vita build has NO network.
 *
 * This replaces V4's CoreCLR-only managed block (Diag/NativeCensus NetBlock, which does nothing for AOT'd direct
 * P/Invokes): every call that would reach the network fails at once with the errno a host without a route would give,
 * so the BCL throws SocketException on the spot and no packet ever exists:
 *   - SystemNative_Socket                  -> EAFNOSUPPORT. new Socket (...) throws SocketException(AddressFamilyNotSupported);
 *                                             Socket.OSSupportsIPv4/IPv6 (SocketProtocolSupportPal) report false, quietly
 *   - Connect / Send / SendMessage / SendFile / Receive* -> ENETUNREACH (no socket can exist, so they are never reached)
 *   - name resolution (GetHostEntryForName, GetNameInfo) -> EAI_FAIL: Dns.* throws SocketException at once
 *   - other per-socket calls               -> EBADF (there is no socket handle)
 *   - interfaces / domain name             -> "no interfaces", "" (NetworkInterface.GetAllNetworkInterfaces () is empty)
 * The socket-address helpers are real (pure data, Linux sockaddr layout), because System.Net.Primitives runs them in
 * type initializers (SocketAddressPal) and for IPEndPoint/SocketAddress, where a failure would be a permanent
 * TypeInitializationException.
 */
#include "pal_config.h"
#include "pal_errno.h"
#include "pal_networking.h"
#include "pal_interfaceaddresses.h"
#include "pal_networkchange.h"
#include "pal_tcpstate.h"

#include <errno.h>
#include <pthread.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

void shepherd_vita_native_log (const char *msg);

static void note_once (volatile int *done, const char *what)
{
	if (!*done) {
		*done = 1;
		shepherd_vita_native_log (what);
	}
}
#define REFUSED(name, err) do { static volatile int d_; note_once (&d_, "net: " name " refused (" err "): the PS Vita build has no network"); } while (0)

/* ---------------------------------------------------------------- socket addresses (Linux layout) */
enum { V_AF_UNSPEC = 0, V_AF_UNIX = 1, V_AF_INET = 2, V_AF_INET6 = 10 };
enum { V_SIN_SIZE = 16, V_SIN6_SIZE = 28, V_SUN_SIZE = 110, V_SUN_PATH_OFFSET = 2, V_SUN_PATH_SIZE = 108, V_STORAGE_SIZE = 128 };
/* sockaddr_in:  u16 family | u16 port (BE) | u32 addr (network order) | 8 zero bytes
 * sockaddr_in6: u16 family | u16 port (BE) | u32 flowinfo | 16 addr | u32 scope_id */

static uint16_t get_family (const uint8_t *sa) { uint16_t f; memcpy (&f, sa, 2); return f; }
static void put_family (uint8_t *sa, uint16_t f) { memcpy (sa, &f, 2); }

static int pal_to_platform (int32_t pal, uint16_t *out)
{
	switch (pal) {
	case AddressFamily_AF_UNSPEC: *out = V_AF_UNSPEC; return 1;
	case AddressFamily_AF_UNIX: *out = V_AF_UNIX; return 1;
	case AddressFamily_AF_INET: *out = V_AF_INET; return 1;
	case AddressFamily_AF_INET6: *out = V_AF_INET6; return 1;
	default: return 0;
	}
}

static int platform_to_pal (uint16_t f, int32_t *out)
{
	switch (f) {
	case V_AF_UNSPEC: *out = AddressFamily_AF_UNSPEC; return 1;
	case V_AF_UNIX: *out = AddressFamily_AF_UNIX; return 1;
	case V_AF_INET: *out = AddressFamily_AF_INET; return 1;
	case V_AF_INET6: *out = AddressFamily_AF_INET6; return 1;
	default: return 0;
	}
}

int32_t SystemNative_GetSocketAddressSizes (int32_t *ipv4SocketAddressSize, int32_t *ipv6SocketAddressSize, int32_t *udsSocketAddressSize, int32_t *maxSocketAddressSize)
{
	if (!ipv4SocketAddressSize || !ipv6SocketAddressSize || !udsSocketAddressSize || !maxSocketAddressSize)
		return Error_EFAULT;
	*ipv4SocketAddressSize = V_SIN_SIZE;
	*ipv6SocketAddressSize = V_SIN6_SIZE;
	*udsSocketAddressSize = V_SUN_SIZE;
	*maxSocketAddressSize = V_STORAGE_SIZE;
	return Error_SUCCESS;
}

int32_t SystemNative_GetMaximumAddressSize (void) { return V_STORAGE_SIZE; }

void SystemNative_GetDomainSocketSizes (int32_t *pathOffset, int32_t *pathSize, int32_t *addressSize)
{
	if (pathOffset) *pathOffset = V_SUN_PATH_OFFSET;
	if (pathSize) *pathSize = V_SUN_PATH_SIZE;
	if (addressSize) *addressSize = V_SUN_SIZE;
}

int32_t SystemNative_GetAddressFamily (const uint8_t *socketAddress, int32_t socketAddressLen, int32_t *addressFamily)
{
	if (!socketAddress || !addressFamily || socketAddressLen < 2)
		return Error_EFAULT;
	if (!platform_to_pal (get_family (socketAddress), addressFamily))
		*addressFamily = AddressFamily_AF_UNKNOWN;
	return Error_SUCCESS;
}

int32_t SystemNative_SetAddressFamily (uint8_t *socketAddress, int32_t socketAddressLen, int32_t addressFamily)
{
	uint16_t f;
	if (!socketAddress || socketAddressLen < 2)
		return Error_EFAULT;
	if (!pal_to_platform (addressFamily, &f))
		return Error_EAFNOSUPPORT;
	put_family (socketAddress, f);
	return Error_SUCCESS;
}

int32_t SystemNative_GetPort (const uint8_t *socketAddress, int32_t socketAddressLen, uint16_t *port)
{
	if (!socketAddress || !port || socketAddressLen < 2)
		return Error_EFAULT;
	uint16_t f = get_family (socketAddress);
	if (f != V_AF_INET && f != V_AF_INET6)
		return Error_EAFNOSUPPORT;
	if (socketAddressLen < (f == V_AF_INET ? V_SIN_SIZE : V_SIN6_SIZE))
		return Error_EFAULT;
	*port = (uint16_t) ((socketAddress [2] << 8) | socketAddress [3]);
	return Error_SUCCESS;
}

int32_t SystemNative_SetPort (uint8_t *socketAddress, int32_t socketAddressLen, uint16_t port)
{
	if (!socketAddress || socketAddressLen < 2)
		return Error_EFAULT;
	uint16_t f = get_family (socketAddress);
	if (f != V_AF_INET && f != V_AF_INET6)
		return Error_EAFNOSUPPORT;
	if (socketAddressLen < (f == V_AF_INET ? V_SIN_SIZE : V_SIN6_SIZE))
		return Error_EFAULT;
	socketAddress [2] = (uint8_t) (port >> 8);
	socketAddress [3] = (uint8_t) port;
	return Error_SUCCESS;
}

int32_t SystemNative_GetIPv4Address (const uint8_t *socketAddress, int32_t socketAddressLen, uint32_t *address)
{
	if (!socketAddress || !address || socketAddressLen < V_SIN_SIZE)
		return Error_EFAULT;
	if (get_family (socketAddress) != V_AF_INET)
		return Error_EINVAL;
	memcpy (address, socketAddress + 4, 4);
	return Error_SUCCESS;
}

int32_t SystemNative_SetIPv4Address (uint8_t *socketAddress, int32_t socketAddressLen, uint32_t address)
{
	if (!socketAddress || socketAddressLen < V_SIN_SIZE)
		return Error_EFAULT;
	if (get_family (socketAddress) != V_AF_INET)
		return Error_EINVAL;
	memcpy (socketAddress + 4, &address, 4);
	return Error_SUCCESS;
}

int32_t SystemNative_GetIPv6Address (const uint8_t *socketAddress, int32_t socketAddressLen, uint8_t *address, int32_t addressLen, uint32_t *scopeId)
{
	if (!socketAddress || !address || !scopeId || socketAddressLen < V_SIN6_SIZE || addressLen < 16)
		return Error_EFAULT;
	if (get_family (socketAddress) != V_AF_INET6)
		return Error_EINVAL;
	memcpy (address, socketAddress + 8, 16);
	memcpy (scopeId, socketAddress + 24, 4);
	return Error_SUCCESS;
}

int32_t SystemNative_SetIPv6Address (uint8_t *socketAddress, int32_t socketAddressLen, uint8_t *address, int32_t addressLen, uint32_t scopeId)
{
	if (!socketAddress || !address || socketAddressLen < V_SIN6_SIZE || addressLen < 16)
		return Error_EFAULT;
	if (get_family (socketAddress) != V_AF_INET6)
		return Error_EINVAL;
	memset (socketAddress + 4, 0, 4);   /* flowinfo */
	memcpy (socketAddress + 8, address, 16);
	memcpy (socketAddress + 24, &scopeId, 4);
	return Error_SUCCESS;
}

/* ---------------------------------------------------------------- refused: sockets */
int32_t SystemNative_Socket (int32_t addressFamily, int32_t socketType, int32_t protocolType, intptr_t *createdSocket)
{
	if (createdSocket)
		*createdSocket = -1;
	REFUSED ("SystemNative_Socket", "EAFNOSUPPORT");
	return Error_EAFNOSUPPORT;
}

#define NET_UNREACH(name, args) \
	int32_t SystemNative_##name args { REFUSED ("SystemNative_" #name, "ENETUNREACH"); return Error_ENETUNREACH; }
NET_UNREACH (Connect, (intptr_t socket, uint8_t *socketAddress, int32_t socketAddressLen))
NET_UNREACH (Send, (intptr_t socket, void *buffer, int32_t bufferLen, int32_t flags, int32_t *sent))
NET_UNREACH (SendMessage, (intptr_t socket, MessageHeader *messageHeader, int32_t flags, int64_t *sent))
NET_UNREACH (SendFile, (intptr_t out_fd, intptr_t in_fd, int64_t offset, int64_t count, int64_t *sent))
NET_UNREACH (Receive, (intptr_t socket, void *buffer, int32_t bufferLen, int32_t flags, int32_t *received))
NET_UNREACH (ReceiveMessage, (intptr_t socket, MessageHeader *messageHeader, int32_t flags, int64_t *received))

#define NO_SOCKET(name, args) int32_t SystemNative_##name args { return Error_EBADF; }
NO_SOCKET (Bind, (intptr_t socket, int32_t protocolType, uint8_t *socketAddress, int32_t socketAddressLen))
NO_SOCKET (Listen, (intptr_t socket, int32_t backlog))
NO_SOCKET (Accept, (intptr_t socket, uint8_t *socketAddress, int32_t *socketAddressLen, intptr_t *acceptedSocket))
NO_SOCKET (Shutdown, (intptr_t socket, int32_t socketShutdown))
NO_SOCKET (Disconnect, (intptr_t socket))
NO_SOCKET (GetPeerName, (intptr_t socket, uint8_t *socketAddress, int32_t *socketAddressLen))
NO_SOCKET (GetSockName, (intptr_t socket, uint8_t *socketAddress, int32_t *socketAddressLen))
NO_SOCKET (GetSocketErrorOption, (intptr_t socket, int32_t *error))
NO_SOCKET (GetSockOpt, (intptr_t socket, int32_t socketOptionLevel, int32_t socketOptionName, uint8_t *optionValue, int32_t *optionLen))
NO_SOCKET (GetRawSockOpt, (intptr_t socket, int32_t socketOptionLevel, int32_t socketOptionName, uint8_t *optionValue, int32_t *optionLen))
NO_SOCKET (SetSockOpt, (intptr_t socket, int32_t socketOptionLevel, int32_t socketOptionName, uint8_t *optionValue, int32_t optionLen))
NO_SOCKET (SetRawSockOpt, (intptr_t socket, int32_t socketOptionLevel, int32_t socketOptionName, uint8_t *optionValue, int32_t optionLen))
NO_SOCKET (GetSocketType, (intptr_t socket, int32_t *addressFamily, int32_t *socketType, int32_t *protocolType, int32_t *isListening))
NO_SOCKET (GetAtOutOfBandMark, (intptr_t socket, int32_t *available))
NO_SOCKET (GetBytesAvailable, (intptr_t socket, int32_t *available))
NO_SOCKET (GetLingerOption, (intptr_t socket, LingerOption *option))
NO_SOCKET (SetLingerOption, (intptr_t socket, LingerOption *option))
NO_SOCKET (SetReceiveTimeout, (intptr_t socket, int32_t millisecondsTimeout))
NO_SOCKET (SetSendTimeout, (intptr_t socket, int32_t millisecondsTimeout))
NO_SOCKET (GetIPv4MulticastOption, (intptr_t socket, int32_t multicastOption, IPv4MulticastOption *option))
NO_SOCKET (SetIPv4MulticastOption, (intptr_t socket, int32_t multicastOption, IPv4MulticastOption *option))
NO_SOCKET (GetIPv6MulticastOption, (intptr_t socket, int32_t multicastOption, IPv6MulticastOption *option))
NO_SOCKET (SetIPv6MulticastOption, (intptr_t socket, int32_t multicastOption, IPv6MulticastOption *option))
NO_SOCKET (TryChangeSocketEventRegistration, (intptr_t port, intptr_t socket, int32_t currentEvents, int32_t newEvents, uintptr_t data))

/* The socket event engine. new SafeSocketHandle () reads SocketAsyncEngine.InlineSocketCompletionsEnabled, which runs
 * SocketAsyncEngine's type initializer BEFORE Interop.Sys.Socket is even called: it creates one event port + buffer and
 * starts the ".NET Sockets" thread, whose loop FailFasts on any WaitForSocketEvents error. So the port is real (a
 * condition nobody signals) and the wait parks that thread for good, in native code (GC-safe under coop suspend, so
 * it never holds up a stop-the-world). Then Socket () fails with EAFNOSUPPORT -> SocketException, as intended, and the
 * type initializer stays healthy (a failure there would be a permanent TypeInitializationException). Costs one parked
 * thread, only once something touches sockets (the menu boot does not: stage A bound no network entry point). */
typedef struct {
	pthread_mutex_t m;
	pthread_cond_t c;
} VitaEventPort;

int32_t SystemNative_CreateSocketEventPort (intptr_t *port)
{
	if (!port)
		return Error_EFAULT;
	VitaEventPort *p = (VitaEventPort *) calloc (1, sizeof (VitaEventPort));
	if (!p || pthread_mutex_init (&p->m, NULL) != 0 || pthread_cond_init (&p->c, NULL) != 0) {
		free (p);
		*port = -1;
		return Error_ENOMEM;
	}
	*port = (intptr_t) p;
	{ static volatile int d; note_once (&d, "net: socket event engine created and parked (sockets are refused; it never gets an event)"); }
	return Error_SUCCESS;
}

int32_t SystemNative_CloseSocketEventPort (intptr_t port) { return Error_SUCCESS; }   /* never closed in practice; the waiter stays parked */

int32_t SystemNative_WaitForSocketEvents (intptr_t port, SocketEvent *buffer, int32_t *count)
{
	VitaEventPort *p = (VitaEventPort *) port;
	if (!p || port == -1)
		return Error_EBADF;
	pthread_mutex_lock (&p->m);
	for (;;)
		pthread_cond_wait (&p->c, &p->m);   /* nothing signals it: no socket can be registered */
}

int32_t SystemNative_CreateSocketEventBuffer (int32_t count, SocketEvent **buffer)
{
	if (!buffer || count < 0)
		return Error_EFAULT;
	*buffer = (SocketEvent *) calloc ((size_t) (count ? count : 1), sizeof (SocketEvent));
	return *buffer ? Error_SUCCESS : Error_ENOMEM;
}

int32_t SystemNative_FreeSocketEventBuffer (SocketEvent *buffer)
{
	free (buffer);
	return Error_SUCCESS;
}
int32_t SystemNative_PlatformSupportsDualModeIPv4PacketInfo (void) { return 0; }
int32_t SystemNative_GetControlMessageBufferSize (int32_t isIPv4, int32_t isIPv6) { return 0; }
int32_t SystemNative_TryGetIPPacketInformation (MessageHeader *messageHeader, int32_t isIPv4, IPPacketInformation *packetInfo) { return 0; }

/* ---------------------------------------------------------------- refused: name resolution */
int32_t SystemNative_GetHostEntryForName (const uint8_t *address, int32_t addressFamily, HostEntry *entry)
{
	if (entry)
		memset (entry, 0, sizeof (*entry));
	REFUSED ("SystemNative_GetHostEntryForName", "EAI_FAIL");
	return GetAddrInfoErrorFlags_EAI_FAIL;
}

void SystemNative_FreeHostEntry (HostEntry *entry) { }

int32_t SystemNative_GetNameInfo (const uint8_t *address, int32_t addressLength, int8_t isIPv6, uint8_t *host, int32_t hostLength,
				  uint8_t *service, int32_t serviceLength, int32_t flags)
{
	REFUSED ("SystemNative_GetNameInfo", "EAI_FAIL");
	return GetAddrInfoErrorFlags_EAI_FAIL;
}

/* ---------------------------------------------------------------- interfaces: there are none */
int32_t SystemNative_GetDomainName (uint8_t *name, int32_t nameLength)
{
	if (!name || nameLength < 1) {
		errno = EINVAL;
		return -1;
	}
	name [0] = 0;
	return 0;
}

uint32_t SystemNative_InterfaceNameToIndex (char *interfaceName) { return 0; }

int32_t SystemNative_EnumerateInterfaceAddresses (void *context, IPv4AddressFound onIpv4Found, IPv6AddressFound onIpv6Found, LinkLayerAddressFound onLinkLayerFound)
{
	return 0;
}

int32_t SystemNative_GetNetworkInterfaces (int32_t *interfaceCount, NetworkInterfaceInfo **interfaces, int32_t *addressCount, IpAddressInfo **addressList)
{
	if (interfaceCount) *interfaceCount = 0;
	if (interfaces) *interfaces = NULL;
	if (addressCount) *addressCount = 0;
	if (addressList) *addressList = NULL;
	return 0;
}

Error SystemNative_CreateNetworkChangeListenerSocket (intptr_t *retSocket)
{
	if (retSocket)
		*retSocket = -1;
	return Error_EAFNOSUPPORT;
}

Error SystemNative_ReadEvents (intptr_t sock, NetworkChangeEvent onNetworkChange) { return Error_EBADF; }

int32_t SystemNative_MapTcpState (int32_t tcpState) { return TcpState_Unknown; }
