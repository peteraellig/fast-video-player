# fast video player (CuePlayer)

CuePlayer is a Windows x64 video player for live shows. Arrange clips in a cue list, preview playback, and send video and audio over NDI, OMT or a supported DeckLink SDI output. Each cue can hold its last frame, go to black, loop, or cut or fade into the next clip.

**[Download the Windows x64 installer · version 0.1.0.32](https://github.com/peteraellig/cueplayer/releases/download/v0.1.0.32/FastVideoPlayer-Setup-0.1.0.32.exe)**

![CuePlayer main screen with cue list and video preview](logo/Mainscreen.png)

## Installation

Run the installer on Windows x64. It includes the .NET runtime. The interface uses Microsoft Edge WebView2. For NDI output, install the NDI runtime. For DeckLink output, install Blackmagic Desktop Video.

## Build from source

Install the .NET 10 SDK on Windows. The project expects the OMT SDK files `libomt.dll` and `libvmx.dll` in `../sdi_converter/third_party/omt/Libraries/Winx64/`, relative to the project directory.

Run `dotnet build CuePlayer.csproj -c Release` to build the player. To create a self-contained installer, install Inno Setup and run `setup\build.cmd`. The installer is written to `publish/`.

## License

CuePlayer is released under the [BSD Zero Clause License (0BSD)](LICENSE). Bundled third-party components retain their respective licenses, listed in the [third-party notices](licence/THIRD-PARTY-NOTICES.txt).
