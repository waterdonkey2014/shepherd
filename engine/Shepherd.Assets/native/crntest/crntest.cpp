// Reference check: unpack level 0 of a .crn with Unity's crn_decomp.h and write raw DXT blocks.
#include <cstdio>
#include <cstdlib>
#include <vector>
#include "../crunch-unity/inc/crn_decomp.h"
int main(int argc, char** argv) {
  if (argc < 3) { fprintf(stderr, "usage: crntest in.crn out.dxt\n"); return 2; }
  FILE* f = fopen(argv[1], "rb"); if (!f) return 3;
  std::vector<unsigned char> d; unsigned char buf[65536]; size_t n;
  while ((n = fread(buf, 1, sizeof buf, f)) > 0) d.insert(d.end(), buf, buf + n);
  fclose(f);
  crnd::crn_texture_info ti; ti.m_struct_size = sizeof(ti);
  if (!crnd::crnd_get_texture_info(d.data(), (crnd::uint32)d.size(), &ti)) { fprintf(stderr, "bad info\n"); return 4; }
  crnd::crnd_unpack_context ctx = crnd::crnd_unpack_begin(d.data(), (crnd::uint32)d.size());
  if (!ctx) { fprintf(stderr, "unpack_begin failed\n"); return 5; }
  unsigned bx = (ti.m_width + 3) / 4, by = (ti.m_height + 3) / 4;
  unsigned bs = crnd::crnd_get_bytes_per_dxt_block(ti.m_format);
  std::vector<unsigned char> out(bx * by * bs);
  void* faces[1] = { out.data() };
  if (!crnd::crnd_unpack_level(ctx, faces, (crnd::uint32)out.size(), bx * bs, 0)) { fprintf(stderr, "unpack_level failed\n"); return 6; }
  crnd::crnd_unpack_end(ctx);
  FILE* o = fopen(argv[2], "wb"); fwrite(out.data(), 1, out.size(), o); fclose(o);
  printf("%ux%u fmt=%d levels=%u -> %zu bytes\n", ti.m_width, ti.m_height, (int)ti.m_format, ti.m_levels, out.size());
  return 0;
}
