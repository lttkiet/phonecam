
import os, time, subprocess

size_mb = 100
test_file = 'test_100mb.bin'
with open(test_file, 'wb') as f:
    f.write(os.urandom(size_mb * 1024 * 1024))

t0 = time.time()
p = subprocess.run(['adb', 'push', test_file, '/data/local/tmp/test_100mb.bin'], capture_output=True, text=True)
dt = time.time() - t0
os.remove(test_file)
subprocess.run(['adb', 'shell', 'rm', '/data/local/tmp/test_100mb.bin'])

print(p.stdout.strip())
print(f'Raw ADB push throughput: {(size_mb * 8) / dt:.2f} Mbps ({(size_mb / dt):.2f} MB/s)')
