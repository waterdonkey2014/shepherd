#!/usr/bin/env python3
"""SHEPHERD-VITA device kit #8 (track jit): ONE DEFINITION for the interpreter and the baseline JIT tier (vita/notes/kit8-jit.md).

Moves the bodies of the interpreter opcodes the baseline tier compiles out of interp.c's main loop into shepjit-ops.h, written once in
terms of a few primitives that each user defines for itself:

  SJ_IP(n) SJ_IP32(n) SJ_IP64(n)   the instruction's operand slot n (16 bit) / READ32 / READ64 at slot n
  SJ_DATA(n)                       frame->imethod->data_items [ip [n]]
  SJ_NEXT(len)                     the instruction is done: on to the next one (len slots)
  SJ_BRANCH(off)                   a branch is taken (off = the offset the interpreter adds to ip)
  SJ_THROW_<KIND>()                the interpreter's own exception for that check
  NULL_CHECK, INIT_VTABLE, SAFEPOINT, BACK_BRANCH_PROFILE, LOCAL_VAR   (as interp.c defines them)

interp.c defines the primitives as exactly the code it had (ip [n], ip += len, THROW_EX (...)), so the preprocessed interpreter is the
same token for token and its object code is unchanged -- the 'ops' build's member guard checks that (vita/toolchain/build-runtime-k8.sh
ops). shepjit-stencils.c defines them as holes the trace compiler patches (operands), tail calls to the next / taken instruction, and
an exit back to the interpreter AT the instruction for every check that would throw (the interpreter then runs the instruction itself
and throws exactly as before). shepjit.c's lockstep walker defines them as the interpreter does, on a copy of the locals.

usage: make-ops.py <interp.c in> <interp.c out> <shepjit-ops.h out>   (run once on a kit #5 LLVM runtime tree; the result is the
k8-jit-ops.patch hunk for interp.c and the new header)
"""
import re, sys

# the opcodes the baseline tier compiles (the kit #8 census, vita/notes/kit8-jit.md section 2: the simple ones of the top ~200 by
# executed count on the idle Main Menu and the New Game route, and their families)
OPS = """
MOV_4 MOV_8 MOV_1 MOV_2 MOV_I4_I1 MOV_I4_U1 MOV_I4_I2 MOV_I4_U2 MOV_8_2 MOV_8_3 MOV_8_4 MOV_VT
LDC_I4_M1 LDC_I4_0 LDC_I4_1 LDC_I4_2 LDC_I4_3 LDC_I4_4 LDC_I4_5 LDC_I4_6 LDC_I4_7 LDC_I4_8 LDC_I4_S LDC_I4 LDC_I8_0 LDC_I8 LDC_I8_S
LDC_R4 LDC_R8 LDLOCA_S INITLOCALS
ADD_I4 ADD_I8 ADD_R4 ADD_R8 ADD1_I4 ADD_I4_IMM ADD1_I8 ADD_I8_IMM SUB_I4 SUB_I8 SUB_R4 SUB_R8 SUB1_I4 SUB1_I8
MUL_I4 MUL_I8 MUL_I4_IMM MUL_I8_IMM ADD_MUL_I4_IMM ADD_MUL_I8_IMM MUL_R4 MUL_R8
DIV_I4 DIV_I8 DIV_R4 DIV_R8 DIV_UN_I4 DIV_UN_I8 REM_I4 REM_I8 REM_R4 REM_R8 REM_UN_I4 REM_UN_I8
AND_I4 AND_I8 OR_I4 OR_I8 XOR_I4 XOR_I8 SHL_I4 SHL_I8 SHR_I4 SHR_I8 SHR_UN_I4 SHR_UN_I8
SHL_I4_IMM SHL_I8_IMM SHR_I4_IMM SHR_I8_IMM SHR_UN_I4_IMM SHR_UN_I8_IMM
NEG_I4 NEG_I8 NEG_R4 NEG_R8 NOT_I4 NOT_I8
CONV_I1_I4 CONV_I1_I8 CONV_I1_R4 CONV_I1_R8 CONV_U1_I4 CONV_U1_I8 CONV_U1_R4 CONV_U1_R8 CONV_I2_I4 CONV_I2_I8 CONV_I2_R4 CONV_I2_R8
CONV_U2_I4 CONV_U2_I8 CONV_U2_R4 CONV_U2_R8 CONV_I4_R4 CONV_I4_R8 CONV_I8_I4 CONV_I8_U4 CONV_I8_R4 CONV_I8_R8
CONV_R4_I4 CONV_R4_I8 CONV_R4_R8 CONV_R8_I4 CONV_R8_I8 CONV_R8_R4
CEQ_I4 CEQ0_I4 CEQ_I8 CEQ_R4 CEQ_R8 CNE_I4 CNE_I8 CNE_R4 CNE_R8 CGT_I4 CGT_I8 CGT_R4 CGT_R8 CGE_I4 CGE_I8 CGE_R4 CGE_R8
CGE_UN_I4 CGE_UN_I8 CGT_UN_I4 CGT_UN_I8 CGT_UN_R4 CGT_UN_R8 CLT_I4 CLT_I8 CLT_R4 CLT_R8 CLT_UN_I4 CLT_UN_I8 CLT_UN_R4 CLT_UN_R8
CLE_I4 CLE_I8 CLE_UN_I4 CLE_UN_I8 CLE_R4 CLE_R8
BR_S BR
BRFALSE_I4_S BRFALSE_I8_S BRFALSE_I4 BRFALSE_I8 BRTRUE_I4_S BRTRUE_I8_S BRTRUE_I4 BRTRUE_I8
BRFALSE_I4_SP BRFALSE_I8_SP BRTRUE_I4_SP BRTRUE_I8_SP
BEQ_I4_S BEQ_I8_S BEQ_R4_S BEQ_R8_S BEQ_I4 BEQ_I8 BEQ_R4 BEQ_R8
BGE_I4_S BGE_I8_S BGE_R4_S BGE_R8_S BGE_I4 BGE_I8 BGE_R4 BGE_R8
BGT_I4_S BGT_I8_S BGT_R4_S BGT_R8_S BGT_I4 BGT_I8 BGT_R4 BGT_R8
BLT_I4_S BLT_I8_S BLT_R4_S BLT_R8_S BLT_I4 BLT_I8 BLT_R4 BLT_R8
BLE_I4_S BLE_I8_S BLE_R4_S BLE_R8_S BLE_I4 BLE_I8 BLE_R4 BLE_R8
BNE_UN_I4_S BNE_UN_I8_S BNE_UN_R4_S BNE_UN_R8_S BNE_UN_I4 BNE_UN_I8 BNE_UN_R4 BNE_UN_R8
BGE_UN_I4_S BGE_UN_I8_S BGE_UN_R4_S BGE_UN_R8_S BGE_UN_I4 BGE_UN_I8 BGE_UN_R4 BGE_UN_R8
BGT_UN_I4_S BGT_UN_I8_S BGT_UN_R4_S BGT_UN_R8_S BGT_UN_I4 BGT_UN_I8 BGT_UN_R4 BGT_UN_R8
BLE_UN_I4_S BLE_UN_I8_S BLE_UN_R4_S BLE_UN_R8_S BLE_UN_I4 BLE_UN_I8 BLE_UN_R4 BLE_UN_R8
BLT_UN_I4_S BLT_UN_I8_S BLT_UN_R4_S BLT_UN_R8_S BLT_UN_I4 BLT_UN_I8 BLT_UN_R4 BLT_UN_R8
BEQ_I4_SP BEQ_I8_SP BGE_I4_SP BGE_I8_SP BGT_I4_SP BGT_I8_SP BLT_I4_SP BLT_I8_SP BLE_I4_SP BLE_I8_SP
BNE_UN_I4_SP BNE_UN_I8_SP BGE_UN_I4_SP BGE_UN_I8_SP BGT_UN_I4_SP BGT_UN_I8_SP BLE_UN_I4_SP BLE_UN_I8_SP BLT_UN_I4_SP BLT_UN_I8_SP
BEQ_I4_IMM_SP BEQ_I8_IMM_SP BGE_I4_IMM_SP BGE_I8_IMM_SP BGT_I4_IMM_SP BGT_I8_IMM_SP BLT_I4_IMM_SP BLT_I8_IMM_SP BLE_I4_IMM_SP BLE_I8_IMM_SP
BNE_UN_I4_IMM_SP BNE_UN_I8_IMM_SP BGE_UN_I4_IMM_SP BGE_UN_I8_IMM_SP BGT_UN_I4_IMM_SP BGT_UN_I8_IMM_SP BLE_UN_I4_IMM_SP BLE_UN_I8_IMM_SP
BLT_UN_I4_IMM_SP BLT_UN_I8_IMM_SP
LDFLD_I1 LDFLD_U1 LDFLD_I2 LDFLD_U2 LDFLD_I4 LDFLD_I8 LDFLD_R4 LDFLD_R8 LDFLD_O LDFLD_VT
STFLD_I1 STFLD_U1 STFLD_I2 STFLD_U2 STFLD_I4 STFLD_I8 STFLD_R4 STFLD_R8 STFLD_O STFLD_VT_NOREF
LDFLDA LDFLDA_UNSAFE CKNULL
LDSFLD_I1 LDSFLD_U1 LDSFLD_I2 LDSFLD_U2 LDSFLD_I4 LDSFLD_I8 LDSFLD_R4 LDSFLD_R8 LDSFLD_O LDSFLDA
STSFLD_I1 STSFLD_U1 STSFLD_I2 STSFLD_U2 STSFLD_I4 STSFLD_I8 STSFLD_R4 STSFLD_R8
LDLEN LDELEM_I1 LDELEM_U1 LDELEM_I2 LDELEM_U2 LDELEM_I4 LDELEM_U4 LDELEM_I8 LDELEM_R4 LDELEM_R8 LDELEM_REF LDELEMA1
STELEM_I1 STELEM_U1 STELEM_I2 STELEM_U2 STELEM_I4 STELEM_I8 STELEM_R4 STELEM_R8 STELEM_VT_NOREF
LDIND_I1 LDIND_U1 LDIND_I2 LDIND_U2 LDIND_I4 LDIND_I8 LDIND_R4 LDIND_R8
LDIND_OFFSET_I1 LDIND_OFFSET_U1 LDIND_OFFSET_I2 LDIND_OFFSET_U2 LDIND_OFFSET_I4 LDIND_OFFSET_I8
LDIND_OFFSET_IMM_I1 LDIND_OFFSET_IMM_U1 LDIND_OFFSET_IMM_I2 LDIND_OFFSET_IMM_U2 LDIND_OFFSET_IMM_I4 LDIND_OFFSET_IMM_I8
STIND_REF STIND_I1 STIND_I2 STIND_I4 STIND_I8 STIND_R4 STIND_R8
STIND_OFFSET_I1 STIND_OFFSET_I2 STIND_OFFSET_I4 STIND_OFFSET_I8 STIND_OFFSET_IMM_I1 STIND_OFFSET_IMM_I2 STIND_OFFSET_IMM_I4 STIND_OFFSET_IMM_I8
LDOBJ_VT STOBJ_VT_NOREF
SAFEPOINT INTRINS_SPAN_CTOR INTRINS_MEMORYMARSHAL_GETARRAYDATAREF GETITEM_LOCALSPAN
LDSTR STRLEN GETCHR LDELEM_VT NEWOBJ_VT_INLINED INITBLK CPBLK
CONV_OVF_I4_U4 CONV_OVF_I4_I8 CONV_OVF_I4_U8 CONV_OVF_I4_R4 CONV_OVF_I4_R8 CONV_OVF_U4_I4 CONV_OVF_U4_I8 CONV_OVF_U4_R4 CONV_OVF_U4_R8
CONV_OVF_I2_I4 CONV_OVF_I2_U4 CONV_OVF_I2_I8 CONV_OVF_I2_U8 CONV_OVF_I2_R4 CONV_OVF_I2_R8 CONV_OVF_U2_I4 CONV_OVF_U2_I8 CONV_OVF_U2_R4 CONV_OVF_U2_R8
CONV_OVF_I1_I4 CONV_OVF_I1_U4 CONV_OVF_I1_I8 CONV_OVF_I1_U8 CONV_OVF_I1_R4 CONV_OVF_I1_R8 CONV_OVF_U1_I4 CONV_OVF_U1_I8 CONV_OVF_U1_R4 CONV_OVF_U1_R8
CONV_OVF_U8_I4 CONV_OVF_U8_I8 CONV_OVF_I8_U8 CONV_OVF_U8_R4 CONV_OVF_U8_R8 CONV_OVF_I8_R4 CONV_OVF_I8_R8
ADD_OVF_I4 ADD_OVF_I8 ADD_OVF_UN_I4 ADD_OVF_UN_I8 MUL_OVF_I4 MUL_OVF_I8 MUL_OVF_UN_I4 MUL_OVF_UN_I8
SUB_OVF_I4 SUB_OVF_I8 SUB_OVF_UN_I4 SUB_OVF_UN_I8
""".split()

THROWS = {
    'interp_get_exception_divide_by_zero (frame, ip)': 'SJ_THROW_DIVIDE_BY_ZERO',
    'interp_get_exception_overflow (frame, ip)': 'SJ_THROW_OVERFLOW',
    'interp_get_exception_index_out_of_range (frame, ip)': 'SJ_THROW_INDEX_OUT_OF_RANGE',
    'interp_get_exception_argument_out_of_range ("length", frame, ip)': 'SJ_THROW_ARGUMENT_OUT_OF_RANGE_LENGTH',
    'interp_get_exception_null_reference(frame, ip)': 'SJ_THROW_NULL_REFERENCE',
}
LEAVE = {'NULL_CHECK', 'INIT_VTABLE', 'SAFEPOINT', 'BACK_BRANCH_PROFILE', 'LOCAL_VAR', 'MONO_DISABLE_WARNING', 'MONO_RESTORE_WARNING'}

def die(m): sys.stderr.write('make-ops.py: ' + m + '\n'); sys.exit(1)

def strip_comments(t):
    t = re.sub(r'/\*.*?\*/', ' ', t, flags=re.S)
    return re.sub(r'//[^\n]*', '', t)

def convert(t, where):
    t = strip_comments(t)
    t = re.sub(r'frame->imethod->data_items\s*\[\s*ip\s*\[\s*(\d+)\s*\]\s*\]', r'SJ_DATA (\1)', t)
    t = re.sub(r'READ32\s*\(\s*ip\s*\+\s*(\d+)\s*\)', r'SJ_IP32 (\1)', t)
    t = re.sub(r'READ64\s*\(\s*ip\s*\+\s*(\d+)\s*\)', r'SJ_IP64 (\1)', t)
    t = re.sub(r'\*\s*\(\s*ip\s*\+\s*(\d+)\s*\)', r'SJ_IP (\1)', t)
    t = re.sub(r'\bip\s*\[\s*(\d+)\s*\]', r'SJ_IP (\1)', t)
    for k, v in THROWS.items():
        t = re.sub(r'THROW_EX\s*\(\s*' + re.escape(k).replace(r'\ ', r'\s*') + r'\s*,\s*ip\s*\)', v + ' ()', t)
    t = re.sub(r'\bip\s*\+=\s*br_offset\s*;', 'SJ_BRANCH (br_offset);', t)
    t = re.sub(r'\bip\s*\+=\s*(\d+)\s*;', r'SJ_NEXT (\1);', t)
    t = re.sub(r'\+\+\s*ip\s*;', 'SJ_NEXT (1);', t)
    if re.search(r'\bip\b', t): die(f'{where}: an ip use make-ops.py cannot convert: {t.strip()[:300]}')
    if re.search(r'\b(context|error|clause_args|THROW_EX|goto|MINT_IN_BREAK)\b', t): die(f'{where}: uses interpreter state: {t.strip()[:300]}')
    return t

def macro_text(name, params, body):
    lines = [l.rstrip() for l in body.strip('\n').split('\n')]
    lines = [l for l in lines if l.strip()]
    head = f'#define {name}{params} '
    if len(lines) == 1: return head + lines[0].strip()
    return head + '\\\n' + ' \\\n'.join('\t' + l.strip() for l in lines)

def main():
    src = open(sys.argv[1]).read()
    # the interpreter's family macros (#define NAME(args) body, multi-line with \): every definition, by position
    defs = {}
    for m in re.finditer(r'^#define\s+([A-Z_0-9]+)\(([^)]*)\)((?:[^\n]*\\\n)*[^\n]*)', src, flags=re.M):
        defs.setdefault(m.group(1), []).append((m.start(), m.group(2), m.group(3).replace('\\\n', '\n')))
    families, fam_order = {}, []
    def use_family(name, pos):
        cands = [d for d in defs.get(name, []) if d[0] < pos]
        if not cands: die(f'family macro {name} has no definition before offset {pos}')
        d = cands[-1]
        if name in families:
            if families[name][1:] != d[1:]: die(f'family macro {name} is defined twice with different bodies')
            return
        families[name] = d
        body = convert(d[2], 'family ' + name)
        for inner in re.findall(r'\b([A-Z][A-Z_0-9]+)\s*\(', body):
            if inner in defs and inner not in LEAVE and not inner.startswith('SJ_'): use_family(inner, d[0])
        fam_order.append(name)
    out_c = src; ops_h = []
    cases = {m.group(1): m for m in re.finditer(r'MINT_IN_CASE\(MINT_([A-Z0-9_]+)\)', src)}
    repl = []
    for op in OPS:
        m = cases.get(op)
        if not m: die(f'MINT_{op}: no handler')
        start = m.end()
        brk = src.index('MINT_IN_BREAK;', start)
        nxt = src.find('MINT_IN_CASE(', start)
        if nxt != -1 and nxt < brk: die(f'MINT_{op}: falls through into another case (shared handler)')
        body = src[start:brk]
        brace = ''
        bs = body.lstrip()
        if bs.startswith('{'):   # MINT_IN_CASE(X) { ... MINT_IN_BREAK; }
            brace = '{'; body = bs[1:]
        if re.search(r'#\s*(if|else|endif|ifdef|ifndef)', body):
            pre, rest = re.split(r'#\s*ifn?def\s+\w+\s*\n|#\s*if\s+[^\n]*\n', body, maxsplit=1)
            cond = re.search(r'#\s*(ifn?def\s+\w+|if\s+[^\n]*)', body).group(1).strip()
            a, rest2 = re.split(r'#\s*else\s*\n', rest, maxsplit=1)
            b, post = re.split(r'#\s*endif[^\n]*\n', rest2, maxsplit=1)
            variants = [(cond, pre + a + post), (None, pre + b + post)]
        else:
            variants = [(None, body)]
        texts = []
        for cond, v in variants:
            cv = convert(v, 'MINT_' + op)
            for fam in re.findall(r'\b([A-Z][A-Z_0-9]+)\s*\(', cv):
                if fam in defs and fam not in LEAVE and not fam.startswith('SJ_'): use_family(fam, m.start())
            cv = re.sub(r'\b([A-Z][A-Z_0-9]+)(\s*\()', lambda mm: ('SJM_' + mm.group(1) if mm.group(1) in families else mm.group(1)) + mm.group(2), cv)
            if brace: cv = '{\n' + cv + '\n}'
            texts.append((cond, cv))
        if len(texts) == 1:
            ops_h.append(macro_text('SJOP_MINT_' + op, '', texts[0][1]))
        else:
            ops_h.append('#' + texts[0][0] + '\n' + macro_text('SJOP_MINT_' + op, '', texts[0][1]) + '\n#else\n' + macro_text('SJOP_MINT_' + op, '', texts[1][1]) + '\n#endif')
        repl.append((m.start(), brk, op, brace))
    # interp.c: each converted handler becomes MINT_IN_CASE(MINT_X) [{] SJOP_MINT_X; MINT_IN_BREAK; [}]
    # (each keeps its line count: the g_assert line numbers after it -- constants in the code -- stay the same, so the object is byte-identical)
    for start, brk, op, brace in sorted(repl, reverse=True):
        head = src[start:src.index(')', start) + 1]
        nl = src.count('\n', start, brk)
        new = head + (' { ' if brace else ' ') + 'SJOP_MINT_' + op + ';' + '\n' * nl + ('\t\t\t' if nl else ' ')
        out_c = out_c[:start] + new + out_c[brk:]
    # interp.c's primitives (shepjit-interp.h, which includes shepjit-ops.h) go in on the blank line after LOCAL_VAR's definition (one line
    # for one line: the line numbers stay)
    prim = '''
/* SHEPHERD kit #8 (vita/notes/kit8-jit.md): the bodies of the opcodes the baseline JIT tier compiles live in shepjit-ops.h, written once
 * for both tiers. Here the primitives they use are the interpreter's own code, so this loop is the same token for token. */
#define SJ_IP(n) ip [n]
#define SJ_IP32(n) READ32 (ip + n)
#define SJ_IP64(n) READ64 (ip + n)
#define SJ_DATA(n) frame->imethod->data_items [ip [n]]
#define SJ_NEXT(len) ip += len
#define SJ_BRANCH(off) ip += off
#define SJ_THROW_DIVIDE_BY_ZERO() THROW_EX (interp_get_exception_divide_by_zero (frame, ip), ip)
#define SJ_THROW_OVERFLOW() THROW_EX (interp_get_exception_overflow (frame, ip), ip)
#define SJ_THROW_INDEX_OUT_OF_RANGE() THROW_EX (interp_get_exception_index_out_of_range (frame, ip), ip)
#define SJ_THROW_ARGUMENT_OUT_OF_RANGE_LENGTH() THROW_EX (interp_get_exception_argument_out_of_range ("length", frame, ip), ip)
#define SJ_THROW_NULL_REFERENCE() THROW_EX (interp_get_exception_null_reference(frame, ip), ip)
#include "shepjit-ops.h"
'''
    anchor = '#define LOCAL_VAR(offset,type) (*(type*)(locals + (offset)))\n\n'
    if out_c.count(anchor) != 1: die('LOCAL_VAR anchor (followed by a blank line)')
    out_c = out_c.replace(anchor, anchor[:-1] + '#include "shepjit-interp.h"   /* SHEPHERD kit #8: the baseline JIT tier shares these opcodes (vita/notes/kit8-jit.md) */\n', 1)
    if out_c.count('\n') != src.count('\n'): die(f'interp.c line count changed: {src.count(chr(10))} -> {out_c.count(chr(10))}')
    open(sys.argv[2], 'w').write(out_c)
    open(sys.argv[3].replace('shepjit-ops.h', 'shepjit-interp.h'), 'w').write('''/* shepjit-interp.h: SHEPHERD-VITA device kit #8 (vita/notes/kit8-jit.md). interp.c's primitives for shepjit-ops.h, the one definition of
 * the opcodes the baseline JIT tier compiles: here they are exactly the code the interpreter had (ip [n], ip += len, THROW_EX (...)),
 * so the interpreter's main loop is the same token for token (and its object code byte for byte: vita/toolchain/build-runtime-k8.sh
 * ops). Included once, inside mono_interp_exec_method's file scope, right after LOCAL_VAR. */''' + prim.replace('#include "shepjit-ops.h"', '#include "shepjit-ops.h"'))
    h = ['/*',
         ' * shepjit-ops.h: SHEPHERD-VITA device kit #8 (vita/notes/kit8-jit.md) -- ONE DEFINITION of the opcodes the baseline JIT tier compiles,',
         ' * included by interp.c (the interpreter: primitives = its own code, so its object code is unchanged), shepjit-stencils.c (the',
         ' * copy-and-patch stencils: operands are holes, branches tail calls, every check that would throw an exit back to the interpreter AT',
         ' * the instruction) and shepjit.c (the lockstep walker: the interpreter\'s primitives on a copy of the locals).',
         ' * GENERATED from interp.c by vita/jit/stencils/make-ops.py (the bodies are interp.c\'s, converted mechanically); edit the bodies',
         ' * here, never in two places. The includer defines: SJ_IP SJ_IP32 SJ_IP64 SJ_DATA SJ_NEXT SJ_BRANCH SJ_THROW_* NULL_CHECK INIT_VTABLE',
         ' * SAFEPOINT BACK_BRANCH_PROFILE LOCAL_VAR (and has frame, locals in scope).',
         ' */', '#ifndef __SHEPJIT_OPS_H__', '#define __SHEPJIT_OPS_H__', '',
         '/* the families (interp.c\'s macros of the same names without SJM_) */']
    for name in fam_order:
        _, params, body = families[name]
        fb = convert(body, 'family ' + name)
        fb = re.sub(r'\b([A-Z][A-Z_0-9]+)(\s*\()', lambda mm: ('SJM_' + mm.group(1) if mm.group(1) in families else mm.group(1)) + mm.group(2), fb)
        h.append(macro_text('SJM_' + name, '(' + params + ')', fb))
    h += ['', '/* the opcodes */'] + ops_h
    h += ['', '/* the list, in this order: the stencils, the trace compiler\'s table and the walker */', '#define SJ_OPS(X) \\']
    h += ['\tX (MINT_%s) \\' % op for op in OPS] + ['\t/* end */', '', '#endif /* __SHEPJIT_OPS_H__ */', '']
    open(sys.argv[3], 'w').write('\n'.join(h))
    print(f'make-ops.py: {len(OPS)} opcodes, {len(fam_order)} families ({" ".join(fam_order)})')

main()
