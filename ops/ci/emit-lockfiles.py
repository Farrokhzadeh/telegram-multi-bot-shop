"""Bootstrap-only capture of public dependency metadata; never runtime configuration."""
from pathlib import Path
import base64

for path in sorted(Path('.').glob('**/packages.lock.json')):
    if '/obj/' not in str(path) and '/bin/' not in str(path):
        print('SHOP_LOCKFILE ' + str(path) + ' ' + base64.b64encode(path.read_bytes()).decode())
