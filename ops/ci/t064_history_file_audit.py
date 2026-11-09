import argparse
import hashlib
import json
import os
import pathlib
import re
import stat

SOURCES={"prod1":9277,"prod2":12,"prod3":2993}

def private_directory(path):
	if not path.is_dir() or path.is_symlink() or path.stat().st_mode & 0o077:
		raise ValueError("Private history directory rejected")
	return path

def audit(root,expected=SOURCES):
	private_directory(root)
	seen=set()
	result={}
	for source,count in expected.items():
		parent=private_directory(root/("teleg-history-archive-"+source))
		directory=private_directory(parent/"teleg-customer-history-reviewed")
		aggregate=hashlib.sha256()
		entries=sorted(directory.iterdir(),key=lambda p:p.name)
		if len(entries)!=count:
			raise ValueError("Historical member count mismatch")
		for path in entries:
			if not re.fullmatch("[0-9a-f]{32}\\.history",path.name) or path.is_symlink():
				raise ValueError("Invalid historical member filename")
			info=path.lstat()
			if not stat.S_ISREG(info.st_mode) or info.st_nlink!=1 or info.st_mode & 0o077 or info.st_size<40 or info.st_size>1048769:
				raise ValueError("Private history file rejected")
			with path.open("rb") as file:
				data=file.read(1048770)
			if len(data)!=info.st_size:
				raise ValueError("Concurrent history file change")
			digest=hashlib.sha256(data).digest()
			if digest in seen or path.lstat().st_size!=info.st_size or path.is_symlink():
				raise ValueError("Duplicate or changed historical ciphertext")
			seen.add(digest)
			aggregate.update(digest)
		result[source]={"encrypted_member_files":len(entries),"private_regular_files_only":True,"unique_ciphertexts":True,"ciphertext_commitment_sha256":aggregate.hexdigest()}
	return {"scope":"read_only_encrypted_history_file_integrity","sources":result,"total_members":len(seen),"per_member_decryption_performed":False,"live_bot_files_mounted":False,"release_ready":False,"matched":True}

def main():
	parser=argparse.ArgumentParser()
	parser.add_argument("--root",type=pathlib.Path,required=True)
	parser.add_argument("--receipt",type=pathlib.Path,required=True)
	args=parser.parse_args()
	if not args.receipt.is_absolute() or args.receipt.exists() or not private_directory(args.receipt.parent):
		raise ValueError("Private receipt rejected")
	result=audit(args.root)
	fd=os.open(str(args.receipt),os.O_CREAT|os.O_EXCL|os.O_WRONLY,0o600)
	with os.fdopen(fd,"w") as file:
		json.dump(result,file,sort_keys=True,indent=2)
		file.write("\n")
		file.flush()
		os.fsync(file.fileno())
	print("PASS: private encrypted member history file integrity; no decryption or runtime mount")
if __name__=="__main__":
	main()
