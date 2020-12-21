#!/bin/bash

script="$(realpath "$0")"
scriptDir="$(dirname "$script")"
export PATH=$scriptDir:"$scriptDir/scripts":$PATH

set -o errexit
set -o nounset

workingDir="$(pwd)"
workingDirWin="$(pwd -W | perl -pe 's/\//\\\\/g')"

# configurable parameters
viewTag=
vobTag=
refDate=
vobDirs=
mergeDirs=
labels=
branches=
excludes=

# read configuration from file, if available
cfgFile="${workingDir}/import.conf"
if [ -r "${cfgFile}" ]; then
    source "${cfgFile}"
fi

# mandatory values
if [ -z "$viewTag" ] || [ -z "$vobTag" ]; then
    echo "View and VOB tags must be configured!"
    exit 1
fi

mvfsKey="HKLM\\SYSTEM\\CurrentControlSet\\services\\Mvfs\\Parameters"
viewDrive="$(reg query $mvfsKey //v drive | perl -ane 'if (/drive/) { print uc pop @F }')"
viewDir="/$viewDrive/$viewTag/$vobTag"
viewDirWin="$viewDrive:\\$viewTag\\$vobTag"

# default values
if [ -z "$refDate" ]; then
    refDate="$(date -Iseconds)"
fi

if [ -z "$vobDirs" ]; then
    vobDirs="*"
fi

cd "$viewDir"
echo "Getting roots..."
roots=()
rootList=()
set +o nounset
for root in $vobDirs; do
    if [ -d "$root" ] && [ "$root" != "lost+found" ]; then
        roots+=("$root")
        rootList+=("-R:$viewDirWin\\$root")
    fi
done
set -o nounset

# prep the setup
labelList=()
if [ ! -z "$labels" ]; then
    for label in $labels; do
        labelList+=("-Labels:$label")
    done
fi

branchList=()
if [ ! -z "$branches" ]; then
    for branch in $branches; do
        branchList+=("-Branches:$branch")
    done
fi

if [ ! -z "$excludes" ]; then
    export CC2GIT_EXCLUDES="$excludes"
fi

# let's go
cd "$workingDir"
if [ ! -d git-import ]; then
    mkdir git-import
fi
cd git-import

if [ ! -d export ]; then
    mkdir export
fi

echo "$refDate" > import-date.txt
cd "$viewDir"

# unless all roots will be put in one repo, run one clearexport_ccase for each directory, so that
# 1) it doesn't crash (out of memory),
# 2) it is parallelized only on folders we can actually see (purely for optimization)
cc_export=0
if [ -z "${mergeDirs}" ]; then
    for root in "${roots[@]}"; do
        real_root="$(basename "$root")"
        if [ ! -f "$workingDir/git-import/export/$real_root.export" ]; then
            echo "Exporting $real_root..."
            clearexport_ccase -r -o "$workingDirWin\\git-import\\export\\$real_root.export" "$real_root" > "$workingDir/git-import/export/$real_root.export.log" &
            cc_export=1
            sleep 5 # give it a bit of time to start
        fi
    done
else
    real_roots=()
    for root in "${roots[@]}"; do
        real_roots+=("$(basename "${root}")")
    done
    if [ ! -f "$workingDir/git-import/export/$vobTag.export" ]; then
        echo "Exporting $vobTag..."
        clearexport_ccase -r -o "$workingDirWin\\git-import\\export\\$vobTag.export" "${real_roots[@]}" > "$workingDir/git-import/export/$vobTag.export.log" &
        cc_export=1
        sleep 5 # give it a bit of time to start
    fi
fi

cd "$workingDir/git-import/export"
if [ $cc_export -eq 1 ]; then
    working=1
    echo -n "Waiting for "
    while [ $working ]; do
        working=
        for f in *.export; do
            # clearcase export files are empty until everything is finished
            if [ ! -s "$f" ]; then
                working=1
                echo -n "${f%.export}"
            fi
        done
        echo -n "..."
        sleep 15
    done
    echo " done!"
fi

cd "$viewDir"
if [ ! -f "$workingDir/git-import/export/all_dirs" ]; then
    echo "Finding directories..."
    cleartool find -all -type d -print | LC_ALL=C sort -r > "$workingDir/git-import/export/all_dirs"
fi

if [ ! -f "$workingDir/git-import/export/all_files" ]; then
    echo "Finding files..."
    cleartool find -all -type f -print | LC_ALL=C sort -r > "$workingDir/git-import/export/all_files"
fi

cd "$workingDir/git-import/export"

if [ ! -f to_import.dirs ]; then
    perl "$scriptDir/filter.pl" all_dirs > to_import.dirs
fi

if [ ! -f to_import.files ]; then
    perl "$scriptDir/filter.pl" all_files > to_import.files
fi

if [ ! -f fullVobDB.bin ]; then
    GitImporter.exe -S:fullVobDB.bin "${labelList[@]}" "${branchList[@]}" -E:to_import.files -D:to_import.dirs -C:"$viewDirWin" -O:"$refDate" -G *.export
    mv "$scriptDir/GitImporter.log" build_vobdb.log
fi

cd "$workingDir/git-import"

if [ ! -f export/fullVobDB.bin ]; then
    echo "File fullVobDB.bin not found"
    exit 1
fi

if [ ! -d git-repo ]; then
    mkdir git-repo
fi

if [ -f "$scriptDir/GitImporter.log" ]; then
    rm "$scriptDir/GitImporter.log"
fi

if [ -z "${mergeDirs}" ]; then
    for r in export/*.export; do
        root="$(basename "${r%.export}")"

        # check if we need to redo
        if [ -d "git-repo/$root" ]; then
            cd "git-repo/$root"
            if [ -z "$(git branch -a)" ]; then
                cd ../..
                rm -rf "git-repo/$root"
            else
                cd ../..
            fi
        fi

        if [ ! -d "git-repo/$root" ]; then
            echo "Importing $root..."
            if [ -e "history-$root.bin.bak" ]; then
                rm "history-$root.bin.bak"
            fi

            GitImporter.exe -L:export/fullVobDB.bin "${labelList[@]}" "${branchList[@]}" -I:../gitignore -H:"history-$root.bin" -C:"$viewDirWin" -N -R:"$viewDirWin\\$root" > "to_import_full_$root"
            mv "$scriptDir/GitImporter.log" "create_changesets-$root.log"

            export GIT_DIR="$workingDir/git-import/git-repo/$root"
            git init "$GIT_DIR"
            git config core.ignorecase false

            GitImporter.exe -C:"$viewDirWin" -F:"to_import_full_$root" -R:"$viewDirWin\\$root" | tee /dev/null | git fast-import --export-marks="git-marks-$root.marks"
            mv "$scriptDir/GitImporter.log" "create_repo-$root.log"

            echo "Repacking repo..."
            git repack -a -d -f
        fi
        unset root
    done
else
    # check if we need to redo
    if [ -d "git-repo/$vobTag" ]; then
        cd "git-repo/$vobTag"
        if [ -z "$(git branch -a)" ]; then
            cd ../..
            rm -rf "git-repo/$vobTag"
        else
            cd ../..
        fi
    fi

    if [ ! -d "git-repo/$vobTag" ]; then
        echo "Importing $vobTag..."
        if [ -e "history-$vobTag.bin.bak" ]; then
            rm "history-$vobTag.bin.bak"
        fi

        GitImporter.exe -L:export/fullVobDB.bin "${labelList[@]}" "${branchList[@]}" -I:../gitignore -H:"history-$vobTag.bin" -C:"$viewDirWin" -N "${rootList[@]}" > "to_import_full_$vobTag"
        mv "$scriptDir/GitImporter.log" "create_changesets-$vobTag.log"

        export GIT_DIR="$workingDir/git-import/git-repo/$vobTag"
        git init "$GIT_DIR"
        git config core.ignorecase false

        GitImporter.exe -C:"$viewDirWin" -F:"to_import_full_$vobTag" "${rootList[@]}" | tee /dev/null | git fast-import --export-marks="git-marks-$vobTag.marks"
        mv "$scriptDir/GitImporter.log" "create_repo-$vobTag.log"

        echo "Repacking repo..."
        git repack -a -d -f
    fi
fi

echo "Done!"
