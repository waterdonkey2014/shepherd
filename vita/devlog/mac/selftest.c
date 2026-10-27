/* devlog client self-test (Mac): the pure parts, built from the same unity file as the apps.
 *   SHA-256 (FIPS 180-4 / NIST CAVP short + long messages), HMAC-SHA256 (RFC 4231 cases 1, 2, 3, 6, 7),
 *   kid / discovery, request and pong MACs as PROTOCOL.md §1 defines them (cross-checked against Python by test.sh),
 *   the logs-only path rule (config names included), name/app/run regexes, sanitising, MAC-shaped text masking, the
 *   full and reduced meta, the tag search at an offset and across piece boundaries, HERE parsing + proof check,
 *   the civil-date math; revision 3: HERE with base= (Codex binding), base paths, server= / server.txt targets, request
 *   targets of both bindings, the config's mode= / clear= (incl. the one-byte reset offset), the log-file writer.
 * Exit 0 = all passed. */
#include "../devlog_unity.c"
#include <stdio.h>
#include <stdlib.h>
#include <sys/stat.h>
#include <time.h>
#include <unistd.h>

static int g_fail, g_pass;
#define CHECK(cond, ...) do { if (cond) g_pass++; else { g_fail++; printf("FAIL %s:%d: ", __FILE__, __LINE__); printf(__VA_ARGS__); printf("\n"); } } while (0)

static void sha_hex(const void *m, size_t n, char out[65])
{
	dl_sha256 c; uint8_t d[32];
	dl_sha256_init(&c); dl_sha256_update(&c, m, n); dl_sha256_final(&c, d); dl_hex(d, 32, out);
}

int main(int argc, char **argv)
{
	char hx[65];
	/* --- SHA-256 */
	sha_hex("", 0, hx);
	CHECK(!strcmp(hx, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"), "sha256('') %s", hx);
	sha_hex("abc", 3, hx);
	CHECK(!strcmp(hx, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"), "sha256(abc) %s", hx);
	const char *m448 = "abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq";
	sha_hex(m448, strlen(m448), hx);
	CHECK(!strcmp(hx, "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1"), "sha256(448 bits) %s", hx);
	const char *m896 = "abcdefghbcdefghicdefghijdefghijkefghijklfghijklmghijklmnhijklmnoijklmnopjklmnopqklmnopqrlmnopqrsmnopqrstnopqrstu";
	sha_hex(m896, strlen(m896), hx);
	CHECK(!strcmp(hx, "cf5b16a778af8380036ce59e7b0492370b249b11e8f07a51afac45037afee9d1"), "sha256(896 bits) %s", hx);
	{   /* one million 'a', fed in odd-sized pieces (exercises the buffering) */
		dl_sha256 c; uint8_t d[32]; char a[997];
		memset(a, 'a', sizeof a);
		dl_sha256_init(&c);
		size_t left = 1000000;
		while (left) { size_t k = left < sizeof a ? left : sizeof a; dl_sha256_update(&c, a, k); left -= k; }
		dl_sha256_final(&c, d); dl_hex(d, 32, hx);
		CHECK(!strcmp(hx, "cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0"), "sha256(1M a) %s", hx);
	}
	/* --- HMAC-SHA256, RFC 4231 */
	{
		/* key / data built from the RFC's descriptions (repeated bytes), the MACs are the RFC's */
		static const char *const MAC[5] = {
			"b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7",
			"5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843",
			"773ea91e36800e46854db8ebd09181a72959098b3ef8c122d9635514ced565fe",
			"60e431591ee0b67f0d8a26aacbf5b77f8e0bc6213728c5140546040f0ee37f54",
			"9b09ffa71b942fcb27635fbcd5b0e944bfdc63644f0713938a7f51535c3a35e2" };
		uint8_t k[131], d[160], o[32];
		size_t kn, dn;
		for (int i = 0; i < 5; i++) {
			switch (i) {
			case 0: memset(k, 0x0b, kn = 20); memcpy(d, "Hi There", dn = 8); break;
			case 1: memcpy(k, "Jefe", kn = 4); memcpy(d, "what do ya want for nothing?", dn = 28); break;
			case 2: memset(k, 0xaa, kn = 20); memset(d, 0xdd, dn = 50); break;
			case 3: memset(k, 0xaa, kn = 131); dn = strlen("Test Using Larger Than Block-Size Key - Hash Key First");
			        memcpy(d, "Test Using Larger Than Block-Size Key - Hash Key First", dn); break;
			default: memset(k, 0xaa, kn = 131);
			        { const char *t7 = "This is a test using a larger than block-size key and a larger than block-size data. "
			                           "The key needs to be hashed before being used by the HMAC algorithm.";
			          dn = strlen(t7); memcpy(d, t7, dn); }
			}
			dl_hmac_sha256(k, kn, d, dn, o); dl_hex(o, 32, hx);
			CHECK(!strcmp(hx, MAC[i]), "hmac RFC 4231 case %d %s", i == 3 ? 6 : i == 4 ? 7 : i + 1, hx);
		}
	}
	/* --- kid / MACs printed for test.sh to compare with Python (hashlib + hmac) */
	const char *tok = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";
	char kid[17], mac[33], reqmac[33], pongmac[33];
	sha_hex(tok, strlen(tok), hx);
	memcpy(kid, hx, 16); kid[16] = 0;
	dl_mac32(tok, "discover|0123456789abcdef", mac);
	dl_mac32(tok, "req|GET|/v1/ping|0123456789abcdef|e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", reqmac);
	dl_mac32(tok, "pong|0123456789abcdef|192.0.2.125|47710", pongmac);
	printf("kid=%s\nmac=%s\nreqmac=%s\npongmac=%s\n", kid, mac, reqmac, pongmac);

	/* --- logs-only path rule (root = /r) */
	dl_cpy(G.root, sizeof G.root, "/r");
	struct { const char *p; int ok; } P[] = {
		{ "/r/probe.txt", 1 }, { "/r/probe.prev.txt", 1 }, { "/r/menu.log", 1 },
		{ "/r/runtime/logs/engine.log", 1 }, { "/r/runtime/logs/v6/simstats-frames.tsv", 1 },
		{ "/r/runtime/logs/a/b/c/x.json", 1 }, { "/r/runtime/logs/a/b/c/d/x.json", 0 },
		{ "/r/devlog/pending/20261007T153000Z-3fa94c0be1d2/probe.txt", 1 },
		{ "/r/devlog/pending/bad/probe.txt", 0 }, { "/r/devlog/pending/20261007T153000Z-3fa94c0be1d2/x/probe.txt", 0 },
		{ "/r/devlog.txt", 0 }, { "/r/DEVLOG.TXT", 0 }, { "/r/env.txt", 0 }, { "/r/args.txt", 0 }, { "/r/pad.txt", 0 },
		{ "/r/hello-env.txt", 0 }, { "/r/settings.json", 0 }, { "/r/readtest.bin", 0 },
		{ "/r/persistent/saves/slot_0.txt", 0 }, { "/r/persistent/settings.json", 0 }, { "/r/managed/notes.txt", 0 },
		{ "/r/pack/shepherd.pack", 0 }, { "/r/data/catalog.json", 0 }, { "/r/../outside.txt", 0 },
		{ "/r/runtime/logs/../../devlog.txt", 0 }, { "/r//probe.txt", 0 }, { "/rx/probe.txt", 0 }, { "/r", 0 },
		{ "/r/.hidden.txt", 0 }, { "/r/runtime/cache/x.txt", 0 }, { "/r/a:b.txt", 0 }, { "/other/probe.txt", 0 },
		{ "/r/devlog/server.txt", 0 }, { "/r/devlog/pending/20261007T153000Z-3fa94c0be1d2.run", 0 },
		/* config files by name (review 2026-10-07: env_gl.txt / args_gl.txt got through) */
		{ "/r/env_gl.txt", 0 }, { "/r/args_gl.txt", 0 }, { "/r/env_gl_audio.txt", 0 }, { "/r/args_gl_audio.txt", 0 },
		{ "/r/hello-stress.txt", 0 }, { "/r/Settings.txt", 0 }, { "/r/menu_gl-env.txt", 0 }, { "/r/envgl.txt", 0 },
		{ "/r/pad.prev.txt", 0 }, { "/r/devlog.prev.txt", 0 }, { "/r/my-config.log", 0 }, { "/r/token.txt", 0 },
		{ "/r/runtime/logs/env.txt", 0 }, { "/r/runtime/logs/a/args_gl.json", 0 },
		{ "/r/menu_gl.txt", 1 }, { "/r/menu_gl.prev.txt", 1 }, { "/r/menu_gl_audio.txt", 1 }, { "/r/menu-aot.txt", 1 },
		{ "/r/hello-managed.txt", 1 }, { "/r/physics-t1.txt", 1 }, { "/r/physics-t1-A-x.txt", 1 },
		{ "/r/physbench.prev.txt", 1 }, { "/r/gltest.txt", 1 }, { "/r/shepherd.txt", 1 },
	};
	for (unsigned i = 0; i < sizeof P / sizeof P[0]; i++)
		CHECK(dl_path_allowed(P[i].p) == P[i].ok, "path rule %s -> %d", P[i].p, !P[i].ok);
	dl_cpy(G.root, sizeof G.root, "ux0:data/shepherd");
	CHECK(dl_path_allowed("ux0:data/shepherd/probe.txt"), "vita path probe.txt");
	CHECK(!dl_path_allowed("ux0:data/shepherd/persistent/saves/x.txt"), "vita saves");
	CHECK(!dl_path_allowed("ux0:data/shepherdx/probe.txt"), "vita prefix");
	CHECK(!dl_path_allowed("ux0:data/shepherd/ux0:x.txt"), "vita colon");

	/* --- regexes */
	CHECK(dl_valid_app("probe") && dl_valid_app("physbench") && dl_valid_app("logtest") && dl_valid_app("a"), "apps");
	CHECK(!dl_valid_app("Probe") && !dl_valid_app("1probe") && !dl_valid_app("") && !dl_valid_app("a/b")
	      && !dl_valid_app("abcdefghijklmnopqrstuvwxyzabcdefg"), "bad apps");
	char run[65];
	for (int i = 0; i < 50; i++) { dl_new_run_id(run); CHECK(dl_valid_run(run) && strlen(run) == 29 && run[8] == 'T' && run[15] == 'Z', "run id %s", run); }
	CHECK(!dl_valid_run("short") && !dl_valid_run("-abcdef") && !dl_valid_run("abc/def") && !dl_valid_run("abc.def"), "bad runs");
	CHECK(dl_valid_name("probe.txt") && dl_valid_name("simstats-frames.tsv") && dl_valid_name("a.json"), "names");
	CHECK(!dl_valid_name("probe.bin") && !dl_valid_name(".x.txt") && !dl_valid_name("a..txt") && !dl_valid_name("a b.txt"), "bad names");

	/* --- sanitising */
	unsigned char s[] = { 'a', 0, '\t', '\n', '\r', 0x1b, 0x01, 0x7f, 0xc3, 0xa9, 'z' };
	unsigned k = dl_sanitize(s, sizeof s);
	CHECK(k == 3 && s[1] == '?' && s[2] == '\t' && s[5] == 0x1b && s[6] == '?' && s[7] == '?' && s[8] == 0xc3, "sanitize k=%u", k);

	/* --- HERE parsing + proof check */
	dl_cpy(G.cfg.token, sizeof G.cfg.token, tok);
	char nonces[2][17] = { "0123456789abcdef", "fedcba9876543210" };
	char m[128], proof[33], line[256];
	snprintf(m, sizeof m, "here|%s|%s|%s", nonces[1], "192.0.2.125", "47710");
	dl_mac32(tok, m, proof);
	uint32_t ip; uint16_t port;
	char base[DL_BASE_MAX];
	snprintf(line, sizeof line, "SHPDLOG1 HERE host=192.0.2.125 port=47710 nonce=%s proof=%s\n", nonces[1], proof);
	CHECK(dl_check_here(line, (const char (*)[17])nonces, 2, &ip, &port, base) && port == 47710 && !base[0], "good HERE");
	CHECK(!dl_check_here(line, (const char (*)[17])nonces, 1, &ip, &port, base), "HERE for a nonce not sent yet");
	snprintf(line, sizeof line, "SHPDLOG1 HERE host=192.0.2.126 port=47710 nonce=%s proof=%s\n", nonces[1], proof);
	CHECK(!dl_check_here(line, (const char (*)[17])nonces, 2, &ip, &port, base), "HERE with a changed host");
	snprintf(line, sizeof line, "SHPDLOG1 HERE host=192.0.2.125 port=47711 nonce=%s proof=%s\n", nonces[1], proof);
	CHECK(!dl_check_here(line, (const char (*)[17])nonces, 2, &ip, &port, base), "HERE with a changed port");
	snprintf(line, sizeof line, "SHPDLOG1 HERE host=192.0.2.125 port=47710 nonce=%s proof=%s\n", nonces[1], "00000000000000000000000000000000");
	CHECK(!dl_check_here(line, (const char (*)[17])nonces, 2, &ip, &port, base), "HERE with a bad proof");
	/* revision 3: the Codex server's HERE carries base=, and its proof covers it */
	snprintf(line, sizeof line, "SHPDLOG1 HERE host=192.0.2.125 port=47710 nonce=%s base=/vita/devlog proof=%s\n", nonces[1], proof);
	CHECK(!dl_check_here(line, (const char (*)[17])nonces, 2, &ip, &port, base), "HERE with base= but the proof without it");
	snprintf(m, sizeof m, "here|%s|%s|%s|%s", nonces[0], "192.0.2.125", "8770", "/vita/devlog");
	dl_mac32(tok, m, proof);
	snprintf(line, sizeof line, "SHPDLOG1 HERE host=192.0.2.125 port=8770 nonce=%s base=/vita/devlog proof=%s\n", nonces[0], proof);
	CHECK(dl_check_here(line, (const char (*)[17])nonces, 2, &ip, &port, base) && port == 8770 && !strcmp(base, "/vita/devlog"), "good Codex HERE (base %s)", base);
	snprintf(line, sizeof line, "SHPDLOG1 HERE host=192.0.2.125 port=8770 nonce=%s base=/vita/devlox proof=%s\n", nonces[0], proof);
	CHECK(!dl_check_here(line, (const char (*)[17])nonces, 2, &ip, &port, base), "Codex HERE with a changed base");
	snprintf(line, sizeof line, "SHPDLOG1 HERE host=192.0.2.125 port=8770 nonce=%s proof=%s\n", nonces[0], proof);
	CHECK(!dl_check_here(line, (const char (*)[17])nonces, 2, &ip, &port, base), "Codex HERE with base= removed");
	{
		char herem[33];
		dl_mac32(tok, "here|0123456789abcdef|192.0.2.125|8770|/vita/devlog", herem);
		printf("heremac=%s\n", herem);
	}

	/* --- base paths, server= / server.txt targets, request targets (PROTOCOL.md §10) */
	{
		const char *goodb[] = { "/vita/devlog", "/a", "/a/b/c/d", "/x-y_z/0", NULL };
		const char *badb[] = { "", "/", "vita", "/vita/", "//x", "/Vita", "/a/b/c/d/e", "/a?b", "/a b", "/a/../b", "/a/./b",
		                       "/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "/aaaaaaaaaa/bbbbbbbbbb/cccccccccc/dddddddddd", NULL };
		for (int i = 0; goodb[i]; i++) CHECK(dl_valid_base(goodb[i]), "base %s", goodb[i]);
		for (int i = 0; badb[i]; i++) CHECK(!dl_valid_base(badb[i]), "bad base '%s'", badb[i]);
		uint32_t tip; uint16_t tport; char tb[DL_BASE_MAX];
		CHECK(dl_parse_target("192.0.2.125", &tip, &tport, tb) && tport == 47710 && !tb[0], "server= ip -> receiver 47710");
		CHECK(dl_parse_target("192.0.2.125:47712", &tip, &tport, tb) && tport == 47712 && !tb[0], "server= ip:port");
		CHECK(dl_parse_target("192.0.2.125:8770", &tip, &tport, tb) && tport == 8770 && !strcmp(tb, "/vita/devlog"), "server= :8770 -> Codex default base");
		CHECK(dl_parse_target("192.0.2.125:9000/x/y", &tip, &tport, tb) && tport == 9000 && !strcmp(tb, "/x/y"), "server= ip:port/base");
		CHECK(!dl_parse_target("192.0.2.125:8770/", &tip, &tport, tb) && !dl_parse_target("192.0.2.125:8770/A", &tip, &tport, tb)
		      && !dl_parse_target("host.local:8770", &tip, &tport, tb) && !dl_parse_target("/vita/devlog", &tip, &tport, tb), "bad server= values");
		dl_sess ss;
		dl_run rr;
		char t[DL_TARGET];
		memset(&ss, 0, sizeof ss); memset(&rr, 0, sizeof rr);
		dl_cpy(rr.app, sizeof rr.app, "logtest"); dl_cpy(rr.run, sizeof rr.run, "20261007T153000Z-3fa94c0be1d2");
		CHECK(!dl_target(&ss, "ping", NULL, NULL, t, sizeof t) && !strcmp(t, "/v1/ping"), "receiver ping %s", t);
		CHECK(!dl_target(&ss, "meta", &rr, NULL, t, sizeof t) && !strcmp(t, "/v1/runs/logtest/20261007T153000Z-3fa94c0be1d2/meta"), "receiver meta %s", t);
		CHECK(!dl_target(&ss, "file", &rr, "logtest.txt", t, sizeof t) && !strcmp(t, "/v1/runs/logtest/20261007T153000Z-3fa94c0be1d2/files/logtest.txt"), "receiver file %s", t);
		dl_cpy(ss.base, sizeof ss.base, "/vita/devlog");
		CHECK(!dl_target(&ss, "ping", NULL, NULL, t, sizeof t) && !strcmp(t, "/vita/devlog/v1/ping"), "codex ping %s", t);
		CHECK(!dl_target(&ss, "meta", &rr, NULL, t, sizeof t) && !strcmp(t, "/vita/devlog/v1/upload?op=meta&app=logtest&run=20261007T153000Z-3fa94c0be1d2"), "codex meta %s", t);
		CHECK(!dl_target(&ss, "file", &rr, "logtest.prev.txt", t, sizeof t)
		      && !strcmp(t, "/vita/devlog/v1/upload?op=file&app=logtest&run=20261007T153000Z-3fa94c0be1d2&name=logtest.prev.txt"), "codex file %s", t);
		CHECK(!dl_target(&ss, "done", &rr, NULL, t, sizeof t) && !strcmp(t, "/vita/devlog/v1/upload?op=done&app=logtest&run=20261007T153000Z-3fa94c0be1d2"), "codex done %s", t);
		/* the longest possible target fits */
		dl_cpy(ss.base, sizeof ss.base, "/aaaaaaaa/bbbbbbbb/cccccccc/dddddddd");
		memset(rr.app, 'a', 32); rr.app[32] = 0; memset(rr.run, 'r', 64); rr.run[64] = 0;
		char nm[65]; memset(nm, 'n', 60); memcpy(nm + 60, ".txt", 5);
		CHECK(!dl_target(&ss, "file", &rr, nm, t, sizeof t), "longest target fits (%zu)", strlen(t));
	}

	/* --- MAC-shaped text masked (the receiver's MACADDR_RE), everything else untouched */
	{
		struct { const char *in, *out; } M[] = {
			{ "FAIL crc 0a:1b:2c:3d:4e:5f mismatch", "FAIL crc xx:xx:xx:xx:xx:xx mismatch" },
			{ "AA-BB-CC-DD-EE-FF", "xx-xx-xx-xx-xx-xx" }, { "a0:1b:2c:3d:4e:5f:60", "xx:xx:xx:xx:xx:xx:60" },
			{ "0a:1b:2c:3d:4e:5", "0a:1b:2c:3d:4e:5" }, { "f0a:1b:2c:3d:4e:5f", "f0a:1b:2c:3d:4e:5f" },
			{ "0a:1b:2c:3d:4e:5fe", "0a:1b:2c:3d:4e:5fe" }, { "2026-10-07T15:30:00Z", "2026-10-07T15:30:00Z" },
			{ "20261007-153000-ff30827", "20261007-153000-ff30827" }, { "", "" },
		};
		for (unsigned i = 0; i < sizeof M / sizeof M[0]; i++) {
			char b[64];
			dl_cpy(b, sizeof b, M[i].in);
			dl_mask_macs(b);
			CHECK(!strcmp(b, M[i].out), "mask %s -> %s", M[i].in, b);
		}
	}

	/* --- meta: app strings cleaned + masked; the reduced meta has only the client's own keys */
	{
		dl_run r;
		memset(&r, 0, sizeof r);
		dl_cpy(r.app, sizeof r.app, "logtest"); dl_cpy(r.run, sizeof r.run, "20261007T153000Z-3fa94c0be1d2");
		dl_cpy(r.title_id, sizeof r.title_id, "SHPD00014"); dl_cpy(r.build, sizeof r.build, "b\x01ld 0a:1b:2c:3d:4e:5f");
		dl_cpy(r.detail, sizeof r.detail, "crc 0a:1b:2c:3d:4e:5f\ttab"); dl_cpy(r.outcome, sizeof r.outcome, "fail");
		dl_cpy(r.platform, sizeof r.platform, "mac"); dl_cpy(r.model, sizeof r.model, "mac"); dl_cpy(r.fw, sizeof r.fw, "posix");
		char full[2048], red[2048];
		dl_build_meta(&r, full, sizeof full, 0);
		dl_build_meta(&r, red, sizeof red, 1);
		CHECK(strstr(full, "\nbuild=b ld xx:xx:xx:xx:xx:xx\n") && strstr(full, "\ndetail=crc xx:xx:xx:xx:xx:xx tab\n")
		      && strstr(full, "\ntitle_id=SHPD00014\n") && strstr(full, "\nfiles="), "full meta:\n%s", full);
		CHECK(!strstr(red, "title_id=") && !strstr(red, "build=") && !strstr(red, "detail=") && !strstr(red, "files=")
		      && strstr(red, "\nmeta=reduced\n") && strstr(red, "\noutcome=fail\n") && strstr(red, "app=logtest\n"), "reduced meta:\n%s", red);
	}

	/* --- tags: found at the offset devlog_begin recorded, also past 32 KiB and across a piece boundary */
	{
		char dir[256], p[300];
		const char *tmp = getenv("TMPDIR");
		snprintf(dir, sizeof dir, "%s/devlog-selftest-%d", tmp && *tmp ? tmp : "/tmp", (int)getpid());
		mkdir(dir, 0755);
		dl_cpy(G.root, sizeof G.root, dir);
		snprintf(p, sizeof p, "%s/scan.txt", dir);
		const char *run = "20261007T153000Z-3fa94c0be1d2";
		long offs[] = { 0, 100, 32768 - 20, 32768 * 3 - 60, 300000 };
		for (unsigned k = 0; k < sizeof offs / sizeof offs[0]; k++) {
			FILE *f = fopen(p, "wb");
			for (long i = 0; i < offs[k]; i++) fputc(i % 64 == 63 ? '\n' : 'a' + (int)(i % 26), f);
			fprintf(f, "DEVLOG run=%s app=logtest (devlog upload id)\nafter\n", run);
			for (long i = 0; i < 5000; i++) fputc('z', f);
			fclose(f);
			char first[65];
			CHECK(dl_file_has_tag(p, run, offs[k]), "tag at offset %ld found via its offset", offs[k]);
			CHECK(!dl_file_has_tag(p, "20261007T153000Z-000000000000", offs[k]), "another run's tag not found (%ld)", offs[k]);
			dl_file_tag(p, first);
			CHECK(offs[k] > (long)DL_TAG_SCAN ? !first[0] : !strcmp(first, run), "first tag at %ld: '%s'", offs[k], first);
			if (offs[k] > 50000) CHECK(!dl_file_has_tag(p, run, 0), "tag at %ld is outside the window at offset 0", offs[k]);
		}
		remove(p);
		rmdir(dir);
	}

	/* --- config: mode= (default ask, typo = ask), clear=1 and the offset of its '1', enabled needs mode != off */
	{
		char dir[256], cp[300];
		const char *tmp = getenv("TMPDIR");
		snprintf(dir, sizeof dir, "%s/devlog-selftest-cfg-%d", tmp && *tmp ? tmp : "/tmp", (int)getpid());
		mkdir(dir, 0755);
		snprintf(cp, sizeof cp, "%s/devlog.txt", dir);
		dl_cpy(G.cfg_path, sizeof G.cfg_path, cp);
		struct { const char *text; int enabled, mode, clear; } C[] = {
			{ "enabled=1\ntoken=%s\n", 1, DEVLOG_MODE_ASK, 0 },
			{ "enabled=1\ntoken=%s\nmode=auto\n", 1, DEVLOG_MODE_AUTO, 0 },
			{ "enabled=1\ntoken=%s\nmode = off # quiet\n", 0, DEVLOG_MODE_OFF, 0 },
			{ "enabled=1\ntoken=%s\nmode=Auto\n", 1, DEVLOG_MODE_ASK, 0 },
			{ "enabled=0\ntoken=%s\nmode=auto\n", 0, DEVLOG_MODE_AUTO, 0 },
			{ "enabled=1\ntoken=%s\nmode=ask\nclear=1\n", 1, DEVLOG_MODE_ASK, 1 },
			{ "enabled=1\ntoken=%s\nclear=0\n", 1, DEVLOG_MODE_ASK, 0 },
			{ "enabled=1\ntoken=%s\n  clear = 1  \r\n", 1, DEVLOG_MODE_ASK, 1 },
		};
		for (unsigned i = 0; i < sizeof C / sizeof C[0]; i++) {
			char body[512];
			int n = snprintf(body, sizeof body, C[i].text, tok);
			FILE *f = fopen(cp, "wb"); fwrite(body, 1, (size_t)n, f); fclose(f);
			dl_cfg c;
			dl_read_config(&c);
			CHECK(c.enabled == C[i].enabled && c.mode == C[i].mode && c.clear == C[i].clear, "config %u: enabled %d mode %d clear %d", i, c.enabled, c.mode, c.clear);
			if (c.clear) CHECK(body[c.clear_off] == '1', "clear offset %ld -> '%c'", c.clear_off, body[c.clear_off]);
			CHECK(c.keep_pending == 2, "retention default keep_pending=2 (%d)", c.keep_pending);
		}
		remove(cp);
		rmdir(dir);
	}

	/* --- the log-file writer: write-through lands at once; buffered waits for 64 lines / a flush */
	{
		char dir[256], lp[300];
		const char *tmp = getenv("TMPDIR");
		snprintf(dir, sizeof dir, "%s/devlog-selftest-lf-%d", tmp && *tmp ? tmp : "/tmp", (int)getpid());
		mkdir(dir, 0755);
		snprintf(lp, sizeof lp, "%s/lf.txt", dir);
		CHECK(devlog_logfile_open(lp, 0, 0) == 0, "logfile open");
		devlog_logfile_write("one");
		CHECK(dlp_file_size(lp) == 4, "write-through: on disk at once (%lld)", (long long)dlp_file_size(lp));
		devlog_logfile_close();
		CHECK(devlog_logfile_open(lp, 1, 1) == 0, "logfile open buffered");
		for (int i = 0; i < 63; i++) devlog_logfile_write("x");
		CHECK(dlp_file_size(lp) == 4, "buffered: 63 lines wait (%lld)", (long long)dlp_file_size(lp));
		devlog_logfile_write("x");
		CHECK(dlp_file_size(lp) == 4 + 128, "buffered: the 64th line writes them (%lld)", (long long)dlp_file_size(lp));
		devlog_logfile_write("tail");
		devlog_logfile_flush();
		CHECK(dlp_file_size(lp) == 4 + 128 + 5, "buffered: flush (%lld)", (long long)dlp_file_size(lp));
		devlog_logfile_close();
		/* what buffering saves on this Mac: 20,000 lines of 80 bytes, write-through vs buffered (printed for test.sh) */
		char l80[81];
		memset(l80, 'L', 80); l80[80] = 0;
		long long us[2];
		for (int b = 0; b < 2; b++) {
			struct timespec a0, a1;
			devlog_logfile_open(lp, 0, b);
			clock_gettime(CLOCK_MONOTONIC, &a0);
			for (int i = 0; i < 20000; i++) devlog_logfile_write(l80);
			devlog_logfile_close();
			clock_gettime(CLOCK_MONOTONIC, &a1);
			us[b] = (a1.tv_sec - a0.tv_sec) * 1000000LL + (a1.tv_nsec - a0.tv_nsec) / 1000;
			CHECK(dlp_file_size(lp) == 20000 * 81, "logfile bench size (%d)", b);
		}
		printf("lf_through_us=%lld\nlf_buffered_us=%lld\n", us[0], us[1]);
		remove(lp);
		rmdir(dir);
	}

	/* --- dates */
	CHECK(dl_days(1970, 1, 1) == 0 && dl_days(2000, 3, 1) == 11017 && dl_days(2026, 10, 7) == 20733, "civil days %ld", dl_days(2026, 10, 7));

	(void)argc; (void)argv;
	printf("selftest: %d passed, %d failed\n", g_pass, g_fail);
	return g_fail ? 1 : 0;
}
