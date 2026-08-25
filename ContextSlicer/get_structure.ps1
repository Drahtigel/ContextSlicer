Remove-Item -ErrorAction SilentlyContinue "project_context.txt"; 
$root = (Get-Item .).FullName; 
Get-ChildItem -Recurse -Include *.cs, *.csproj, *.html, *.js, *.css, *.txt | Where-Object { $_.FullName -notlike "*\obj\*" -and $_.FullName -notlike "*\bin\*" -and $_.FullName -notlike "*\.git\*" -and $_.Name -ne "project_context.txt" } | ForEach-Object { $relPath = $_.FullName.Replace($root, "."); 
	Add-Content -Path "project_context.txt" -Value "`n`n=== FILE: $relPath ==="; 
	Add-Content -Path "project_context.txt" -Value (Get-Content $_.FullName -Raw) }
