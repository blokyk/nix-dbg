# `nix-dap`

This repo contains a C# application (in [`nix-repl-adapter`](./nix-repl-adapter/))
providing a wrapper around nix's "debugger" REPL that allows it to be used with
the Debug Adapter Protocol. There is also a basic vscode extension (in [`vscode`](./vscode))
that allows using it from vscode (including snippets and configuration knobs info).

> [!NOTE]
> For now, this only works with [Lix](https://lix.systems)

## Building and using

> [!WARNING]
> **This project is NOT ready for general use.**
> [Just look at the "roadmap" for fuck's sake](#roadmapsupported-features).
> Like there's just a bunch of useless logging everywhere and weird writes
> to my personal dev folder, this is aggressively anti-user. Even *I* don't
> use this myself right now (though I'll probably start to clean up once
> I'm done with variable values).
>
> I'm mostly putting it out there to others the motivation to actually do
> something about this: it can be easily done (hope) AND surely *you* can
> do better than this cursed code (horror).
>
> (also it's undeniable i'd be very happy if this was interesting or useful
> to anyone else.)

> todo: [write a nix shell for this](https://github.com/blokyk/nix-dbg/issues/3) (if you see this please ping me)

> todo: [package debugger as derivation](https://github.com/blokyk/nix-dbg/issues/1) (if you see this please ping me)

0. Fix dev paths: `sed -i sed 's/\/home\/blokyk\/dev\/lab\/nix-dbg/'"$(pwd | sed 's/\//\\\//g')"'/g' nix-repl-adapter/src/{Program,NixDebugger}.cs vscode/src/extension`
1. Build C# project: `pushd nix-repl-adapter && dotnet build && popd`
2. Build extension: `pushd vscode && npm run build && popd`
3. Open vscode: `code vscode/sampleWorkspace`
4. Install extension: run `Extension: Install from VSIX` command and pick the
   VSIX file from `vscode/`
5. Start debugging (generally by hitting F5)
6. Pray it won't hang/crash :D

## Roadmap/supported features

> todo: move these to issues (but keep a succinct list for users)

- [ ] Stop random hangs (real) (100% impossible) challenge
- [x] Break on breakpoint
- [ ] Break on/display errors (for non-breakpoints)
- [x] Step/continue (w/ no eval errors until next break)
- [ ] Step/continue (w/ eval errors)
- [x] Call stack
- [x] Variables list
- [ ] Lexical scopes (Variable values)
- [ ] Arbitrary expression values
  - [ ] watch
  - [ ] debug console
- [ ] Debug console
  - [ ] Intercept commands
    - [ ] :q / :r
    - [ ] continue / step
    - [ ] :a / :add
    - [ ] :l / :lf
    - [ ] :e (open in current vscode window?)
    - [ ] :doc
    - [ ] :env / :env $n
    - [ ] :te (this won't affect us anyway, in theory)
    - [ ] :?
    - [ ] [builds (:b, :bl, :sh, :u, :i, :log)](https://github.com/blokyk/nix-dbg/issues/5)
    - [ ] :t
    - [ ] :p
  - [ ] Intercept raw exprs
  - [ ] Intercept errors/breaks in exprs (and commands like :t and :p)
  - [ ] Allow stopping eval that takes too long
- [ ] Launch configuration
  - [ ] `pure` -- defaults to false, except if filename is `flake.nix`
  - [ ] `breakOnTrace` -- defaults to false
  - [ ] `ignoreTry` -- defaults to true
  - [ ] `allowIFD` -- defaults to false
- [ ] Handle trace messages during eval correctly
