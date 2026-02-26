# VS Code Nix Debug

This is a starter sample for developing VS Code debug adapters.

**Nix Debug** simulates a debug adapter for Visual Studio Code.
It supports *step*, *continue*, *breakpoints*, *exceptions*, and
*variable access* but it is not connected to any real debugger.

The sample is meant as an educational piece showing how to implement a debug
adapter for VS Code. It can be used as a starting point for developing a real adapter.

More information about how to develop a new debug adapter can be found
[here](https://code.visualstudio.com/docs/extensions/example-debuggers).
a
## Using Nix Debug

* Install the **Nix Debug** extension in VS Code.
* Create a new 'program' file `readme.md` and enter several lines of arbitrary text.
* Switch to the debug viewlet and press the gear dropdown.
* Select the debug environment "Nix Debug".
* Press the green 'play' button to start debugging.

You can now 'step through' the `readme.md` file, set and hit breakpoints, and run into exceptions (if the word exception appears in a line).

![Nix Debug](images/nix-debug.gif)

## Build and Run

* Clone the project [https://github.com/Microsoft/vscode-nix-debug.git](https://github.com/Microsoft/vscode-nix-debug.git)
* Open the project folder in VS Code.
* Press `F5` to build and launch Nix Debug in another VS Code window.
* In the explorer view of the new window open the 'program' file `readme.md`
* Set some breakpoints
* From the editor's "Run and Debug" toolbar dropdown menu select "Debug File"