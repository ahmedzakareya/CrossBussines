@echo off
rem Hospital prototype: copies the theme assets from the app's own wwwroot on first run (they are not
rem duplicated in git), then serves on http://localhost:8765 with caching off and opens the browser.
cd /d "%~dp0"
set SRC=%~dp0..\..\..\CrossBuy\wwwroot\Backend-assets
if not exist assets\css\style.bundle.css (
  robocopy "%SRC%\css" assets\css style.bundle.css style.bundle.rtl.css crossbuy-brand.css >nul
  robocopy "%SRC%\plugins\global" assets\plugins\global plugins.bundle.css plugins.bundle.rtl.css plugins.bundle.js >nul
  robocopy "%SRC%\plugins\global\fonts" assets\plugins\global\fonts /E >nul
  robocopy "%SRC%\js" assets\js scripts.bundle.js >nul
  robocopy "%SRC%\media\avatars" assets\media\avatars 300-*.jpg >nul
  robocopy "%SRC%\media\flags" assets\media\flags saudi-arabia.svg egypt.svg united-arab-emirates.svg kuwait.svg jordan.svg >nul
  robocopy "%SRC%\media\svg\avatars" assets\media\svg\avatars blank.svg >nul
)
start "" http://localhost:8765/index.html
python serve.py
