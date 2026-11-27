#!/usr/bin/env python3
import os
import re
import secrets
import stat
import sys
from pathlib import Path

STAGE=Path("/var/lib/akanti-staging")
ROOT=STAGE/"sqlite-private"
OWNER=10001

def require(predicate,message):
	if not predicate: raise ValueError(message)

def safe_directory(path,owner):
	if path.exists():
		mode=path.lstat().st_mode
		require(stat.S_ISDIR(mode) and not stat.S_ISLNK(mode),"Unsafe stage directory")
	else: path.mkdir(mode=0o700)
	os.chmod(path,0o700);os.chown(path,owner,owner)

def private_file(path,data,owner):
	require(isinstance(data,bytes) and 0<len(data)<131072,"Unsafe stage source data")
	if path.exists():
		existing=path.lstat()
		require(stat.S_ISREG(existing.st_mode) and existing.st_nlink==1 and not stat.S_ISLNK(existing.st_mode),"Unsafe existing stage file")
		require(path.read_bytes()==data,"Stage file cannot change in place")
		return
	fd=os.open(path,os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW,0o600)
	try:
		with os.fdopen(fd,"wb") as output: output.write(data)
		os.chown(path,owner,owner);os.chmod(path,0o400 if owner==OWNER else 0o600)
	except BaseException:
		path.unlink(missing_ok=True);raise

def read_private(path,limit):
	stats=path.lstat()
	require(stat.S_ISREG(stats.st_mode) and stats.st_nlink==1 and not stat.S_ISLNK(stats.st_mode) and 0<stats.st_size<=limit and stats.st_mode&0o077==0,"Unsafe private source")
	return path.read_bytes()

def provision_tester(preview):
	require(re.fullmatch(r"/var/lib/teleg-cli/akanti-cold-staging-[A-Za-z0-9_-]+/telegram-preview-[A-Za-z0-9_-]+/config",str(preview)),"Unrecognized synthetic preview path")
	safe_directory(ROOT,0);safe_directory(ROOT/"tester",OWNER)
	for name in ("bot-id","bot-token","tester-id"): private_file(ROOT/"tester"/name,read_private(preview/name,200),OWNER)
	key=ROOT/"tester"/"api-key"
	if not key.exists(): private_file(key,secrets.token_hex(32).encode("ascii"),OWNER)
	require(re.fullmatch(rb"[a-f0-9]{64}",read_private(key,64)) is not None,"Invalid stage API key")

def main():
	require(os.geteuid()==0,"Staging provisioning requires root")
	require(len(sys.argv)==3 and sys.argv[1]=="--plaintext","Plaintext staging provisioning requires an explicit preview path")
	provision_tester(Path(sys.argv[2]).resolve(strict=True))
	print("Synthetic SQLite API and Telegram identity files are provisioned.")

if __name__=="__main__": main()
