# Import ClearCase to Git

This tool is based on the excellent work of [lanfeust69](https://github.com/lanfeust69) with the [clearcase-to-git-importer](https://github.com/lanfeust69/clearcase-to-git) and is able to import a ClearCase VOB to a Git repository (or several different repositories). 

It has been tested on a ClearCase VOB with 17 years of history resulting in a Git repository with a size of ~500MB. The import time was about 2-3 days. It has also been tested on a much smaller VOB that imported in some hours.

## Major differences in this fork from lanfeust69

- Without doubt lower code base quality since I'm unfamiliar with C# and I didn't bother to clean up the code (sorry).
- Supports extracting subfolders in the VOB to different Git repositories.
- Will drop useless or empty commits and labels.
- Significantly more aggressive with merging multiple ClearCase checkins to a Git commit.
- More aggressive with trying to keep the labels correct at a slight cost of the correctness of the history.
- Imports the author and date of a label.
- If labels are not matched properly then the Git tag will be annotated with a message.
- Better support for some edge cases, though there are still many unsolved ones remaining.
- Partial support for converting charset to UTF-8 in metadata. Filenames are known to not be converted properly. Non-ASCII characters in branches and labels can result in mojibake and eventual crash.
- Support for incremental imports has been dropped. It's all or nothing!
- Renaming of files is ignored and instead it leaves everything to Git to figure out.

It has only been tested with a dynamic ClearCase view. Support for thirdparties has not been tested.

## General principles

- Export as much as possible using `clearexport_ccase` (in several parts due to memory constraints of `clearexport_ccase`).
- Get all elements (files and directories).
- Optionally edit these lists to exclude uninteresting stuff (or use `excludes` parameter, see below).
- Use `GitImporter` (which calls `cleartool`) to create (and save) a representation of the VOB.
- Import with `GitImporter` and `git fast-import`. `cleartool` is then used only to get the content of files. This is done repeatedly if importing to multiple Git repositories, though not more than needed.

## Compiling

If you're lucky you might be able to compile with this command:

```
C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /debug /define:DEBUG /define:TRACE /r:protobuf-net.dll /out:scripts\GitImporter.exe /pdb:scripts\GitImporter *.cs
```

## Usage

This tool must be run with Git Bash on Windows.

Please look inside the `scripts` folder and investigate `import.sh`; this is the wrapper that executes both export and import processes in one go. Also make sure to copy the `gitignore` file to a working directory (where you can also create a parameter file, [see below](#parameter-file)) and modify it according to your needs in order to check it in automagically.

The provided `import.sh` will take a VOB and create several Git repositories from it, assuming all folders in the VOB are equivalent to different Git repositories.

For example, the following ClearCase VOB...

```
my_vob
├── somefolder/
|   └── file.txt
└── anotherfolder/
    └── file2.txt
```

... will be transformed to...

```
somefolder/
├── .git/
└── file.txt
anotherfolder/
├── .git/
└── file2.txt
```

When you run `import.sh`, the Git repositories will be created in a folder named `git-import`.

### Parameter file

Since there are quite a few configuration parameters, `import.sh` can read their values from a configuration file called `ìmport.conf` in a working directory. Currently supported parameters are:

- `viewTag`: view tag (mandatory)
- `vobTag`: VOB tag (mandatory; no leading backslash required to avoid bash escaping)
- `refDate` : upper cutoff date for import, corresponds to Origin Date in GitImporter (default: current date)
- `vobDirs` : folders in VOB to import, glob expressions can be used (default: `*`)
- `mergeDirs` : if set to `true`, folders are merged rather than split into separate repos
- `labels` : if set, only the specified labels are imported to Git (can be set to `NONE` to ignore the labels)
- `branches` : if set, only the specified branches are imported to Git
- `excludes` : a regexp to exclude additional subfolders or files from import on the fly (rather than editing import lists manually)

`vobDirs`, `labels` and `branches` can also be specified as quoted space-separated list (e.g. `"A B"`).
