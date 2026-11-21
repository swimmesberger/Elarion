.PHONY: check test check-repo check-commits

# The repository's own checks, everything that runs offline in seconds: the tools' tests and the layout
# check. They need python3 and nothing else. The framework's build and tests are `dotnet` and `npm`
# commands (AGENTS.md, "Commands"); the commit check needs the remote's default branch: `make check-commits`.
check: test check-repo

test:
	python3 -m unittest discover -s tools/tests

check-repo:
	python3 tools/check_repo.py

check-commits:
	python3 tools/check_commits.py
