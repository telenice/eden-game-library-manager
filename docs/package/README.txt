EDEN GAME LIBRARY MANAGER
=========================

Browse the games on an AeroFoil server, download the ones you want, and keep
them in a folder that the Eden emulator reads. You need an AeroFoil account
from whoever runs the server.


GETTING STARTED
---------------

Windows
  Extract the ZIP anywhere and run EdenGameLibraryManager.exe. If Windows
  SmartScreen shows a warning, click "More info", then "Run anyway".

macOS
  Open the .dmg and drag the app into Applications. The first time you open
  it, macOS may block it: open System Settings > Privacy & Security, scroll
  down and click "Open Anyway".

Linux
  Make the .AppImage executable (right-click > Properties > "Allow executing",
  or run: chmod +x on the file), then double-click it. The .tar.gz version
  includes install.sh, which adds the app to your application menu.


FIRST RUN
---------

A short setup asks for:

  1. Your AeroFoil server address (for example https://aerofoil.example.com),
     username and password. Click "Test Connection" to check them.
  2. The folder to save games in, for example D:\Switch Games.
  3. An optional storage limit. Leave it at 0 for no limit.


SET UP EDEN (ONE TIME)
----------------------

  1. In Eden, add your games folder to the game list (double-click the empty
     game list, or add it under game directories in Eden's settings).
  2. Each game is saved in its own subfolder, so right-click the folder in
     Eden's game list and enable "Scan Subfolders".
  3. Updates and DLC are downloaded next to their game. If Eden doesn't pick
     them up, install them with File > Install Files to NAND.


USING THE APP
-------------

Available Games
  Games on the server that you don't have yet. Click Download to get a game
  together with its latest update and DLC. Click a game to see its details
  and download individual items.
  Tick "Group by game" to see one card per game. A green "Update available"
  badge means there's newer content for a game you already have.

Downloaded Games
  Everything in your games folder. "Pin" protects a game from automatic
  clean-up, "Open Folder" shows the file, and "Delete" removes it (only the
  game file: your saves and emulator settings are never touched).

Downloads
  Progress of each download. You can pause, resume, cancel and retry.
  Unfinished downloads continue when you restart the app.

Settings
  Change the server, password, games folder, storage limit and more. You can
  add several servers and switch between them at the top left.


TROUBLESHOOTING
---------------

"Sign-in failed"
  Check your username and password. After several wrong attempts the server
  may block you for a few minutes.

"Only Tinfoil/CyberFoil clients" or "encrypted catalogue"
  The server is set up to block this app. Ask the person who runs it.

"Server unavailable" / Offline
  The app shows the last list of games it saw and you can still manage your
  downloaded games. Downloads resume once the server is reachable again.

Slow downloads
  Speed depends on the server owner's upload speed and your connection.
  Settings > Downloads has a speed limit: make sure it's set to 0.

A game doesn't appear in Eden
  Check that "Scan Subfolders" is enabled for your games folder in Eden, then
  refresh Eden's game list.


YOUR DATA
---------

If you tick "Remember password", it is stored encrypted using your system's
secure storage (Windows data protection, the macOS Keychain, or the Linux
keyring). It is only ever sent to your AeroFoil server.

Settings, logs and unfinished downloads are kept in:
  Windows: %LOCALAPPDATA%\EdenGameLibraryManager
  macOS:   ~/Library/Application Support/EdenGameLibraryManager
  Linux:   ~/.local/share/EdenGameLibraryManager

To uninstall, delete the app and that folder. Your downloaded games stay in
your games folder.


This app only downloads from servers you have an account on. You are
responsible for having the rights to the content you download.
