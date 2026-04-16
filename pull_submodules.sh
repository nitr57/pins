#!/usr/bin/env bash

excluded_path="NINA/External"
while read -r _ path; do
  if [ "$path" = "$excluded_path" ]; then
    echo "Skipping submodule $path"
    continue
  fi

  echo "Updating submodule $path"
  if git submodule update --init --recursive --remote "$path"; then
    continue
  fi

  echo "Remote update failed for $path; falling back to pinned commit" >&2
  git submodule update --init --recursive "$path"
done < <(git config -f .gitmodules --get-regexp '^submodule\..*\.path$')
