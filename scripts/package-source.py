"""Create the take-home source zip with Git history and without working secrets."""
import argparse
from pathlib import Path
import subprocess
import zipfile


def package(destination):
    root = Path(subprocess.check_output(["git", "rev-parse", "--show-toplevel"], text=True).strip())
    git_dir = Path(subprocess.check_output(["git", "rev-parse", "--absolute-git-dir"], text=True).strip())
    tracked = subprocess.check_output(["git", "ls-files", "-z"]).decode().split("\0")
    destination = Path(destination).resolve()
    destination.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(destination, "w", zipfile.ZIP_DEFLATED) as archive:
        for name in filter(None, tracked):
            archive.write(root / name, "Squash-Take-Home/" + name)
        for path in git_dir.rglob("*"):
            if not path.is_file():
                continue
            relative = path.relative_to(git_dir)
            # Hooks are machine-specific, and CI may inject auth into config.
            if relative.parts[0] in {"hooks", "config"} or path.name.endswith(".lock"):
                continue
            archive.write(path, "Squash-Take-Home/.git/" + relative.as_posix())
        archive.writestr("Squash-Take-Home/.git/config", '[core]\n\trepositoryformatversion = 0\n\tfilemode = true\n\tbare = false\n\tlogallrefupdates = true\n[remote "origin"]\n\turl = https://github.com/aravinds-kannappan/Squash-Take-Home.git\n\tfetch = +refs/heads/*:refs/remotes/origin/*\n[branch "main"]\n\tremote = origin\n\tmerge = refs/heads/main\n')
    print(destination)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("destination", nargs="?", default="artifacts/squash-source.zip")
    package(parser.parse_args().destination)
