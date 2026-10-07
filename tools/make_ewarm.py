#!/usr/bin/env python3
"""Generates the IAR EWARM project files (ewarm/*.ewp, *.ewd, *.eww).

The option blocks come from a project saved by IAR EWARM 9.x (the
Teacup_Firmware_iar project, tools/ewarm_template.ewp/.ewd), the file list
from the source tree. Run again after adding or removing source files:

    python3 tools/make_ewarm.py
"""
import glob, os, re

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TEMPLATE_EWP = os.path.join(ROOT, 'tools', 'ewarm_template.ewp')
TEMPLATE_EWD = os.path.join(ROOT, 'tools', 'ewarm_template.ewd')
OUT = os.path.join(ROOT, 'ewarm')

CHIPS = [
    # name, define, ICF, startup, chip menu, debugger ddf, flash loader
    ('STM32F401CC', 'STM32F401xC', 'stm32f401xc_flash.icf', 'startup_stm32f401xc.s', 'FlashSTM32F401xC.board'),
    ('STM32F401CE', 'STM32F401xE', 'stm32f401xe_flash.icf', 'startup_stm32f401xe.s', 'FlashSTM32F401xE.board'),
]

INCLUDES = ['$PROJ_DIR$\\..', '$PROJ_DIR$\\..\\cmsis\\core', '$PROJ_DIR$\\..\\cmsis\\device',
            '$PROJ_DIR$\\..\\driver']
PREINCLUDE = '$PROJ_DIR$\\..\\driver\\cnc3018_defaults.h'


def files(pattern):
    return sorted(os.path.relpath(p, ROOT).replace('/', '\\') for p in glob.glob(os.path.join(ROOT, pattern)))


def group(name, entries, indent='    '):
    out = [indent + '<group>', indent + '    <name>%s</name>' % name]
    for e in entries:
        if isinstance(e, tuple):
            out += group(e[0], e[1], indent + '    ')
        else:
            out += [indent + '    <file>', indent + '        <name>$PROJ_DIR$\\..\\%s</name>' % e, indent + '    </file>']
    out.append(indent + '</group>')
    return out


def set_option(text, name, states):
    """Replace the state(s) of every <option> called name."""
    def rep(m):
        head = m.group(1)
        indent = re.search(r'\n(\s*)<name>', head).group(1)
        body = ''.join('\n%s<state>%s</state>' % (indent, s) for s in states)
        return head + body + '\n' + indent[:-4] + '</option>'
    pat = re.compile(r'(<option>\s*<name>%s</name>(?:\s*<version>\d+</version>)?)(?:\s*<state>[^<]*</state>|\s*<state/>)*\s*</option>' % re.escape(name))
    text, n = pat.subn(rep, text)
    if n == 0:
        raise SystemExit('option %s not found' % name)
    return text


def set_first_option_in(text, section, name, states):
    """Same, only inside <settings><name>section</name> blocks."""
    parts = re.split(r'(<settings>\s*<name>[A-Z]+</name>)', text)
    for i in range(1, len(parts), 2):
        if parts[i].endswith('<name>%s</name>' % section):
            parts[i + 1] = set_option(parts[i + 1].split('</settings>')[0], name, states) + \
                '</settings>' + '</settings>'.join(parts[i + 1].split('</settings>')[1:])
    return ''.join(parts)


def make_ewp(chip):
    name, define, icf, startup, _ = chip
    t = open(TEMPLATE_EWP).read()
    t = set_option(t, 'CCDefines', [define])
    t = set_option(t, 'CCIncludePath2', INCLUDES)
    t = set_first_option_in(t, 'ICCARM', 'PreInclude', [PREINCLUDE])
    t = set_option(t, 'IlinkIcfFile', ['$PROJ_DIR$\\..\\cmsis\\linker\\%s' % icf])
    t = set_option(t, 'OGChipSelectEditMenu', ['%s\tST %s' % (name, name)])
    t = set_option(t, 'GFPUDeviceSlave', ['%s\tST %s' % (name, name)])
    t = t.replace('Teacup_STM32F411_import_lib.o', 'grbl_%s_import_lib.o' % name)
    t = set_option(t, 'OOCOutputFile', ['grbl_%s.bin' % name])
    # Raw binary next to the .out in both configurations (for dfu-util).
    t = set_option(t, 'OOCOutputFormat', ['3'])
    t = set_option(t, 'OOCObjCopyEnable', ['1'])
    # Plain char is unsigned, as in the ARM ABI and the GCC test build.
    t = set_option(t, 'CCSignedPlainChar', ['0'])
    # Optimization. grblHAL doesn't fit into 256 KB without: Debug medium,
    # Release high / size.
    cfgs = t.split('<configuration>')
    for i, c in enumerate(cfgs[1:], 1):
        debug = '<name>Debug</name>' in c[:200]
        lvl, strat = ('2', '0') if debug else ('3', '1')
        c = set_option(c, 'CCOptLevel', [lvl])
        c = set_option(c, 'CCOptLevelSlave', [lvl])
        c = set_option(c, 'CCOptStrategy', [strat])
        # Both projects live in ewarm/: separate output directories per chip.
        cfg = 'Debug' if debug else 'Release'
        for opt, sub in (('ExePath', 'Exe'), ('ObjPath', 'Obj'), ('ListPath', 'List')):
            c = set_option(c, opt, ['%s\\%s\\%s' % (cfg, name, sub)])
        cfgs[i] = c
    t = '<configuration>'.join(cfgs)
    # The core needs malloc(): the heap is defined in the ICF file.
    groups = []
    groups += group('cmsis', ['cmsis\\system_stm32f4xx.c', 'cmsis\\startup\\%s' % startup])
    groups += group('driver', files('driver/*.c') + files('driver/*.h') + [('boards', files('driver/boards/*.h'))])
    groups += group('grbl', files('grbl/*.c') + [('kinematics', files('grbl/kinematics/*.c'))])
    start = t.index('    <group>')
    end = t.index('    <projectSettings>') if '<projectSettings>' in t else t.index('</project>')
    # Single files at the top level of the template (Teacup's app) go as well.
    t = t[:start] + '\n'.join(groups) + '\n' + t[end:]
    open(os.path.join(OUT, 'grbl_%s.ewp' % name), 'w', newline='\r\n').write(t)


def make_ewd(chip):
    name, define, icf, startup, board = chip
    t = open(TEMPLATE_EWD).read()
    t = t.replace('STM32F401CC.ddf', '%s.ddf' % name)
    t = t.replace('FlashSTM32F401xC.board', board)
    open(os.path.join(OUT, 'grbl_%s.ewd' % name), 'w', newline='\r\n').write(t)


os.makedirs(OUT, exist_ok=True)
for c in CHIPS:
    make_ewp(c)
    make_ewd(c)
eww = ['<?xml version="1.0" encoding="UTF-8"?>', '<workspace>']
for c in CHIPS:
    eww += ['    <project>', '        <path>$WS_DIR$\\grbl_%s.ewp</path>' % c[0], '    </project>']
eww += ['    <batchBuild />', '</workspace>', '']
open(os.path.join(OUT, 'grbl_BlackPill.eww'), 'w', newline='\r\n').write('\n'.join(eww))
print('written:', sorted(os.listdir(OUT)))
