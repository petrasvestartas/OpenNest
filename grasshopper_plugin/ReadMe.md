"/Applications/Rhino 8.app/Contents/Resources/bin/yak" build --platform mac
"/Applications/Rhino 8.app/Contents/Resources/bin/yak" login
"/Applications/Rhino 8.app/Contents/Resources/bin/yak" push opennest-2.11.0-rh8_0-mac.yak

cd c:\brg\2_code\compas_wood\src\rhino\gh\package_manager
"C:\Program Files\Rhino 8\System\Yak.exe" spec
Update manifest.yml file
"C:\Program Files\Rhino 8\System\Yak.exe" build --platform win
"C:\Program Files\Rhino 8\System\Yak.exe" login
"C:\Program Files\Rhino 8\System\Yak.exe" push opennest-2.10.0-rh8_0-win.yak