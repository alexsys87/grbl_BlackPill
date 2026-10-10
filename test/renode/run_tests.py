#!/usr/bin/env python3
"""Renode emulation test of the grblHAL firmware for the CNC 3018 BlackPill.

Builds nothing. Expects
  test/gcc/build_F401C_1/grbl.elf  (make TEST=1, host port on USART1)   or
  test/gcc/build_F401C_0/grbl.elf  (make, host port on USB CDC, GRBL_PORT=usb)

Drives the grbl protocol over the host port, observes the answers, GPIO
writes (step pulses, spindle, coolant) and timer registers and checks the
results.

Usage: run_tests.py [renode] [grbl.elf]
  GRBL_PORT=usb  run the same tests over the USB CDC port
                 (model models/TeacupSTM32_OTGFS.cs), default is USART1.
  GRBL_CHIP=F411 the STM32F411CE build (make CHIP=F411E) on a 96 MHz
                 platform, default is F401 (STM32F401CC).
"""
import os, re, subprocess, sys, tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
PORT = os.environ.get('GRBL_PORT', 'uart')
RENODE = sys.argv[1] if len(sys.argv) > 1 else 'renode'
CHIP = os.environ.get('GRBL_CHIP', 'F401')     # F401 or F411
BUILD = {'F401': 'F401C', 'F411': 'F411E'}[CHIP]
MHZ = {'F401': 84, 'F411': 96}[CHIP]
ELF = sys.argv[2] if len(sys.argv) > 2 else os.path.join(
    HERE, '../gcc/build_%s_%d/grbl.elf' % (BUILD, 0 if PORT == 'usb' else 1))
REPL = os.path.join(HERE, 'stm32%s.repl' % CHIP.lower())
HOSTDEV = 'usb' if PORT == 'usb' else 'usart1'

GPIOA_BSRR = 0x40020018
TIM1 = 0x40010000

lines = []
def cmd(c): lines.append(c)
def mark(name): cmd('echo "@@MARK %s"' % name)
def run(t): cmd('emulation RunFor "%s"' % t)
def send(text):
    for ch in text:
        cmd('sysbus.%s WriteChar 0x%02X' % (HOSTDEV, ord(ch)))
def rt(code): cmd('sysbus.%s WriteChar 0x%02X' % (HOSTDEV, code))   # real time command
def send2(text):                                                       # second UART (WiFi port)
    for ch in text:
        cmd('sysbus.usart2 WriteChar 0x%02X' % ord(ch))
def pin(port, n, level): cmd('sysbus.gpioPort%s OnGPIO %d %s' % (port, n, 'true' if level else 'false'))
def monitor_path(path):
    # Quoted monitor strings accept Windows paths and spaces. A bare @path
    # with backslashes/spaces is not portable across the two operating systems.
    return '"%s"' % os.path.abspath(path).replace('\\', '/')

def peek(tag, addr):
    cmd('echo "@@VAL %s"' % tag); cmd('sysbus ReadDoubleWord 0x%08X' % addr)

cmd('include %s' % monitor_path(os.path.join(HERE, 'models', 'TeacupSTM32_OTGFS.cs')))
cmd('mach create "grbl"')
cmd('machine LoadPlatformDescription %s' % monitor_path(REPL))
cmd('sysbus LoadELF %s' % monitor_path(ELF))
cmd('showAnalyzer sysbus.%s Antmicro.Renode.Analyzers.LoggingUartAnalyzer' % HOSTDEV)
cmd('logLevel 3')
cmd('logLevel -1 sysbus.%s' % HOSTDEV)
cmd('showAnalyzer sysbus.usart2 Antmicro.Renode.Analyzers.LoggingUartAnalyzer')
cmd('logLevel -1 sysbus.usart2')
cmd('logLevel -1 sysbus.gpioPortA')
cmd('logLevel -1 sysbus.gpioPortB')

# Inputs have pull-ups, the switches go to GND (normally open). Renode
# doesn't model pull-ups: drive the idle (high) level.
def inputs_idle():
    for n in (6, 7, 8, 9, 12, 13, 14, 15):
        pin('B', n, True)
inputs_idle()

mark('boot'); run('0.6')
if PORT == 'usb':
    cmd('sysbus.usb Open')                     # host opens the port (DTR)
    run('0.1')
cmd('sysbus LogPeripheralAccess sysbus.gpioPortA true')

# --- protocol --------------------------------------------------------------
mark('info'); send('$I\n'); run('0.1')
mark('settings'); send('$$\n'); run('0.3')
mark('status0'); send('?'); run('0.05')
# After an error grblHAL rejects all G-code until a soft reset (Ctrl-X):
# the sender has to stop and reset.
mark('bad'); send('G5000\n'); run('0.05')
mark('bad2'); send('G21\n'); run('0.05')
mark('bad_reset'); rt(0x18); run('0.3')

# --- motion ----------------------------------------------------------------
# 1 mm at 10 mm/s with 800 steps/mm: 800 X steps.
mark('move_x'); send('G21 G91 G1 X1 F600\n'); run('0.6')
mark('status_x'); send('?'); run('0.05')
mark('move_yz'); send('G1 Y-0.5 Z0.25 F300\n'); run('0.6')
mark('status_yz'); send('?'); run('0.05')
# Back to 0 and a half circle, 2 mm diameter.
mark('home0'); send('G90 G0 X0 Y0 Z0\n'); run('0.5')
mark('arc'); send('G2 X2 Y0 I1 J0 F600\n'); run('1.0')
mark('status_arc'); send('?'); run('0.05')

# --- real time commands: feed hold / resume ----------------------------------
mark('hold_move'); send('G91 G1 X5 F300\n'); run('0.4')
mark('hold'); rt(ord('!')); run('0.4')
mark('status_hold'); send('?'); run('0.05')
mark('hold_still'); run('0.3')
mark('resume'); rt(ord('~')); run('1.5')
mark('status_resume'); send('?'); run('0.05')
# Jog and jog cancel.
mark('jog'); send('$J=G91 X10 F600\n'); run('0.2')
rt(0x85); run('0.3')
mark('status_jog'); send('?'); run('0.05')
send('G90 G0 X0 Y0 Z0\n'); run('1.5')

# --- spindle and coolant ----------------------------------------------------
mark('m3'); send('M3 S5000\n'); run('0.1')
peek('ccr', TIM1 + 0x34); peek('arr', TIM1 + 0x2C); peek('ccer', TIM1 + 0x20)
peek('odrb_m3', 0x40020414)
mark('m4'); send('M4 S10000\n'); run('0.1')
peek('ccr4', TIM1 + 0x34); peek('odrb_m4', 0x40020414)
mark('m5'); send('M5\n'); run('0.1')
peek('bdtr5', TIM1 + 0x44); peek('odrb_m5', 0x40020414)
mark('m8'); send('M8\n'); run('0.05'); peek('odrb_m8', 0x40020414)
mark('m7'); send('M7\n'); run('0.05'); peek('odrb_m7', 0x40020414)
mark('m9'); send('M9\n'); run('0.05'); peek('odrb_m9', 0x40020414)

# --- settings in flash -------------------------------------------------------
mark('set100'); send('$100=400\n'); run('0.5')
peek('nvs', 0x08004000)
mark('move400'); send('G91 G1 X1 F600\n'); run('0.6')
send('$100=800\n'); run('0.5')

# --- limit switches ------------------------------------------------------------
# Not fitted ($450=0, the default): the input is ignored even with hard limits on.
mark('lim_on'); send('$21=1\n'); run('0.3')
mark('nolim_move'); send('G91 G1 X2 F600\n'); run('0.1')
pin('B', 12, False)                             # X input low: would be a closed switch
run('0.1')
mark('nolim_status'); send('?'); run('0.05')
pin('B', 12, True); run('0.3')
# Fitted on X ($450=1): the switch stops the machine.
mark('lim_fit'); send('$450=1\n'); run('0.3')
mark('lim_move'); send('G91 G1 X10 F600\n'); run('0.3')
pin('B', 12, False)                             # X switch closes
run('0.2')
pin('B', 12, True)
mark('lim_status'); send('?'); run('0.05')
mark('lim_unlock'); rt(0x18); run('0.3'); send('$X\n'); run('0.1')
mark('lim_off'); send('$21=0\n'); run('0.3')

# --- second UART (USART2, e.g. WiFi) in parallel with the host port ----------
run('0.6')                                      # host port quiet
mark('u2_status'); send2('?'); run('0.05')
mark('u2_cmd'); send2('G91 G1 X1 F600\n'); run('0.6')
# The host runs a move, the WiFi port's command waits for it.
mark('u2_busy'); send('G91 G1 X3 F300\n'); run('0.1')
send2('$I\n'); run('0.2')
mark('u2_wait'); run('0.05')
mark('u2_after'); run('1.5')
# Feed hold / cycle start from the WiFi port while the host's move runs.
mark('u2_hold_move'); send('G91 G1 X3 F300\n'); run('0.8')
send2('!'); run('0.3')
mark('u2_hold_status'); send('?'); run('0.05')
send2('~'); run('1.0')
mark('u2_resumed'); send('?'); run('0.05')
# An error on the WiFi port doesn't lock the host's G-code (grblHAL keeps
# G-code locked after an error until an empty line or a reset).
run('0.6')
mark('u2_err'); send2('G5000\n'); run('0.6')
mark('host_after_u2_err'); send('G91 G0 X0\n'); run('0.2')

# --- probe ----------------------------------------------------------------------
mark('probe'); send('G91 G38.2 Z-5 F120\n'); run('0.3')
pin('B', 15, False)                             # tool touches the plate
run('0.3')
pin('B', 15, True)
mark('probe_done'); run('0.1')

# --- control input: feed hold button ------------------------------------------
mark('btn_move'); send('G1 X3 F300\n'); run('0.3')
pin('B', 7, False); run('0.1'); pin('B', 7, True)
mark('btn_status'); run('0.2'); send('?'); run('0.05')
rt(0x18); run('0.2')

# Reboot (ESC, Ctrl-T): the settings must come back from flash. Last test:
# Renode doesn't reset its timer models on a system reset, so no motion
# afterwards.
send('$110=1234\n'); run('0.5')
mark('reboot'); rt(0x1B); rt(0x14); run('0.001')
inputs_idle()                                   # the reset clears the GPIO model
run('0.8')
if PORT == 'usb':
    cmd('sysbus.usb Open'); run('0.1')
mark('get110'); send('$110\n'); run('0.1')

mark('end')
cmd('quit')

log_dir = os.path.abspath(os.environ.get('GRBL_LOG_DIR', tempfile.gettempdir()))
os.makedirs(log_dir, exist_ok=True)
with tempfile.NamedTemporaryFile(mode='w', encoding='utf-8', suffix='.resc',
                                 prefix='grbl_%s_%s_' % (CHIP, PORT),
                                 dir=log_dir, delete=False) as script_file:
    script_file.write('\n'.join(lines) + '\n')
    script = script_file.name
log_path = os.path.join(log_dir, 'grbl_test_%s_%s.log' % (CHIP, PORT))
if not os.path.isfile(ELF):
    print('Firmware ELF is missing: %s' % ELF, file=sys.stderr)
    sys.exit(2)
proc = subprocess.Popen([RENODE, '--console', '--disable-gui', script],
                        stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                        stderr=subprocess.STDOUT, text=True,
                        encoding='utf-8', errors='replace')
timed_out = False
try:
    out, _ = proc.communicate(timeout=int(os.environ.get('GRBL_TEST_TIMEOUT_SEC', '600')))
except subprocess.TimeoutExpired:
    timed_out = True
    if os.name == 'nt':
        subprocess.run(['taskkill', '/PID', str(proc.pid), '/T', '/F'],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    else:
        proc.kill()
    out, _ = proc.communicate()
out = re.sub(r'\x1b\[[0-9;]*m', '', out)
with open(log_path, 'w', encoding='utf-8') as log_file:
    log_file.write(out)
print('Renode log: %s' % log_path)
print('Monitor script: %s' % script)
if timed_out:
    print('Renode exceeded GRBL_TEST_TIMEOUT_SEC; see the log.', file=sys.stderr)
    sys.exit(2)

# Split the log into sections by markers.
sections, cur = {}, 'pre'
for l in out.splitlines():
    m = re.search(r'@+MARK (\S+)', l)
    if m and 'echo' not in l:
        cur = m.group(1); sections[cur] = []; continue
    sections.setdefault(cur, []).append(l)

if proc.returncode != 0 or 'end' not in sections:
    print('Renode did not complete the monitor script (exit %s); see %s' %
          (proc.returncode, log_path), file=sys.stderr)
    print('\n'.join(out.splitlines()[-30:]), file=sys.stderr)
    sys.exit(2)

vals, tag = {}, None
for l in out.splitlines():
    m = re.search(r'@@VAL (\S+)', l)
    if m and 'echo' not in l:
        tag = m.group(1); continue
    m = re.match(r'^(0x[0-9A-Fa-f]+)\s*$', l.strip())
    if tag and m:
        vals[tag] = int(m.group(1), 16); tag = None

def uart(sec, dev=HOSTDEV):
    return [re.sub(r'^.*\] ', '', l) for l in sections.get(sec, []) if '%s: [host' % dev in l]

def uart2(sec):
    return uart(sec, 'usart2')

def bsrr_writes(sec, port='A'):
    for l in sections.get(sec, []):
        m = re.search(r'gpioPort%s: .*WriteUInt32 to 0x18 \(BitSet\), value 0x([0-9A-F]+)' % port, l)
        if m:
            yield int(m.group(1), 16)

def pulses(sec, bit, port='A'):
    return sum(1 for v in bsrr_writes(sec, port) if v & (1 << bit))

def status(sec):
    for l in uart(sec):
        if l.startswith('<'):
            return l
    return ''

def mpos(sec):
    m = re.search(r'MPos:(-?[\d.]+),(-?[\d.]+),(-?[\d.]+)', status(sec))
    return tuple(float(v) for v in m.groups()) if m else None

fails = 0
def check(name, cond, info=''):
    global fails
    print('%s  %-44s %s' % ('PASS' if cond else 'FAIL', name, info if not cond else ''))
    if not cond:
        fails += 1

print('--- %s, host port: %s ---' % (CHIP, PORT))
boot = uart('boot') + uart('info')
if PORT == 'uart':
    check('boot: welcome message', any(l.startswith('GrblHAL 1.1f') for l in boot), boot)
info = uart('info')
check('$I: board name', '[BOARD:CNC 3018 BlackPill]' in info, info)
check('$I: driver, %d MHz' % MHZ, any(l.startswith('[DRIVER:STM32%s' % CHIP) and '%dMHz' % MHZ in l for l in info), info)
check('$I: settings in flash', any('[NVS STORAGE:*FLASH' in l for l in info), info)
s = uart('settings')
check('$$: 800 steps/mm default', any(re.match(r'\$100=800\.0+$', l) for l in s), [l for l in s if l.startswith('$100')])
check('$$: NO limit switches ($5=7)', '$5=7' in s, [l for l in s if l.startswith('$5=')])
check('$$: no limit switches fitted ($450=0)', '$450=0' in s, [l for l in s if l.startswith('$450')])
check('status report: Idle', status('status0').startswith('<Idle|MPos:0.000,0.000,0.000'), status('status0'))
check('unsupported G code: error:20', 'error:20' in uart('bad'), uart('bad'))
check('after an error: G-code blocked', any(l.startswith('error:') for l in uart('bad2')), uart('bad2'))

n = pulses('move_x', 0) + pulses('status_x', 0)
check('G1 X1: 800 X steps', n == 800, n)
check('G1 X1: position', mpos('status_x') == (1.0, 0.0, 0.0), status('status_x'))
ny, nz = pulses('move_yz', 6), pulses('move_yz', 4)
check('G1 Y-0.5 Z0.25: 400 Y / 200 Z steps', (ny, nz) == (400, 200), (ny, nz))
# grbl convention: direction output high = negative direction ($3=0).
dirw = [v for v in bsrr_writes('move_yz') if v & ((1 << 7) | (1 << 23))]
check('Y direction pin (PA7) high (negative move)', dirw and dirw[-1] & (1 << 7), [hex(v) for v in dirw[:3]])
check('position after Y/Z move', mpos('status_yz') == (1.0, -0.5, 0.25), status('status_yz'))
nx, ny = pulses('arc', 0), pulses('arc', 6)
check('G2 half circle: 1600 X / ~1600 Y steps', nx == 1600 and abs(ny - 1600) <= 4, (nx, ny))
check('G2 end position', mpos('status_arc') == (2.0, 0.0, 0.0), status('status_arc'))

check('feed hold: Hold state', status('status_hold').startswith('<Hold'), status('status_hold'))
check('feed hold: no steps while held', pulses('hold_still', 0) == 0, pulses('hold_still', 0))
total = sum(pulses(sec, 0) for sec in ('hold_move', 'hold', 'status_hold', 'hold_still', 'resume', 'status_resume'))
check('resume: move completes (4000 steps)', total == 4000, total)
check('resume: Idle at X7', status('status_resume').startswith('<Idle') and mpos('status_resume')[0] == 7.0,
      status('status_resume'))
p = mpos('status_jog')
check('jog cancel: stopped early', status('status_jog').startswith('<Idle') and p and 7.0 < p[0] < 17.0, status('status_jog'))

ccr, arr = vals.get('ccr'), vals.get('arr')
check('M3 S5000: PWM 50 % (TIM1)', ccr is not None and arr and abs(ccr / (arr + 1) - 0.5) < 0.01, (ccr, arr))
# (The Renode timer model has no BDTR/MOE, only the channel enable is checked.)
check('M3: PWM channel enabled (CC1E)', vals.get('ccer', 0) & 1, hex(vals.get('ccer', 0)))
check('M3: spindle enable PB1 high, dir PB2 low', (vals.get('odrb_m3', 0) >> 1) & 3 == 1, hex(vals.get('odrb_m3', 0)))
check('M4 S10000: PWM 100 %, dir PB2 high', (vals.get('odrb_m4', 0) >> 1) & 3 == 3 and vals.get('ccr4', 0) >= arr,
      (hex(vals.get('odrb_m4', 0)), vals.get('ccr4')))
check('M5: spindle enable low', not (vals.get('odrb_m5', 0) & 2), hex(vals.get('odrb_m5', 0)))
check('M8: flood PB4 on', vals.get('odrb_m8', 0) & (1 << 4), hex(vals.get('odrb_m8', 0)))
check('M7: mist PB5 on', vals.get('odrb_m7', 0) & (1 << 5), hex(vals.get('odrb_m7', 0)))
check('M9: coolant off', not (vals.get('odrb_m9', 0) & (3 << 4)), hex(vals.get('odrb_m9', 0)))

check('$100=400: ok', 'ok' in uart('set100'), uart('set100'))
check('settings written to flash sector 1', vals.get('nvs', 0xFFFFFFFF) != 0xFFFFFFFF, vals.get('nvs'))
n = pulses('move400', 0)
check('G1 X1 with 400 steps/mm: 400 steps', n == 400, n)

ns = status('nolim_status')
check('$450=0: limit input ignored (no alarm, no Pn:X)', ns.startswith('<Run') and 'Pn:X' not in ns and
      not any(l.startswith('ALARM') for l in uart('nolim_move') + uart('nolim_status')), ns)
check('$450=1: ok', 'ok' in uart('lim_fit'), uart('lim_fit'))
ls = status('lim_status')
check('hard limit: alarm', ls.startswith('<Alarm'), ls + ' ' + ' '.join(uart('lim_move')))
check('hard limit: ALARM:1 reported', any(l.startswith('ALARM:1') for l in uart('lim_move') + uart('lim_status')),
      uart('lim_move'))

u2s = uart2('u2_status')
check('USART2: status report on "?"', any(l.startswith('<Idle') for l in u2s), u2s)
check('USART2: command runs, ok to USART2 only', 'ok' in uart2('u2_cmd') and 'ok' not in uart('u2_cmd') and
      pulses('u2_cmd', 0) == 800, (uart2('u2_cmd'), uart('u2_cmd'), pulses('u2_cmd', 0)))
check('USART2: waits while the host move runs', not any('[BOARD' in l for l in uart2('u2_busy') + uart2('u2_wait')),
      uart2('u2_busy') + uart2('u2_wait'))
after = uart2('u2_after')
check('USART2: answered after the move', any('[BOARD:CNC 3018 BlackPill]' in l for l in after) and 'ok' in after, after)
check('host: no answers of USART2 commands', not any('[BOARD' in l for l in uart('u2_busy') + uart('u2_wait') + uart('u2_after')),
      uart('u2_after'))
check('host: ok for its move', 'ok' in uart('u2_busy'), uart('u2_busy'))
check('USART2: feed hold (!) holds the host move', status('u2_hold_status').startswith('<Hold'), status('u2_hold_status'))
check('USART2: cycle start (~) resumes', status('u2_resumed').startswith('<Idle'), status('u2_resumed'))
check('USART2: error answered on USART2', any(l.startswith('error:') for l in uart2('u2_err')), uart2('u2_err'))
check('host: not locked by the USART2 error', 'ok' in uart('host_after_u2_err') and
      not any(l.startswith('error') for l in uart('host_after_u2_err')), uart('host_after_u2_err'))

pr = uart('probe') + uart('probe_done')
check('G38.2: probe result [PRB:...:1]', any(re.match(r'\[PRB:.*:1\]', l) for l in pr), pr)
check('feed hold button (PB7): Hold', status('btn_status').startswith('<Hold'), status('btn_status'))

rb = uart('reboot') + uart('get110')
if PORT == 'uart':
    check('reboot: welcome message', any(l.startswith('GrblHAL') for l in rb), rb)
check('after reboot: $110=1234 from flash', any(re.match(r'\$110=1234\.0+$', l) for l in uart('get110')), uart('get110'))

print('%d failed' % fails if fails else 'all passed')
sys.exit(1 if fails else 0)
