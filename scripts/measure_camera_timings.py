
import subprocess, time

# Clear logcat
subprocess.run(['adb', 'logcat', '-c'])

# Read a few seconds of logcat
