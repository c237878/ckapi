Add-Type -Path "D:\Code\ckapi\bin\Debug\net8.0\ckapi.dll" -ErrorAction SilentlyContinue
$conn = New-Object System.Data.SQLite.SQLiteConnection("Data Source=D:\ckplayer.db")
$conn.Open()

# List tables
Write-Host "=== Tables ==="
$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name"
$reader = $cmd.ExecuteReader()
while ($reader.Read()) { Write-Host $reader[0] }
$reader.Close()

# For each table, show schema
Write-Host "`n=== Schemas ==="
$cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name"
$reader = $cmd.ExecuteReader()
$tables = @()
while ($reader.Read()) { $tables += $reader[0] }
$reader.Close()

foreach ($t in $tables) {
    Write-Host "`n--- $t ---"
    $cmd.CommandText = "PRAGMA table_info($t)"
    $r = $cmd.ExecuteReader()
    while ($r.Read()) {
        $cid = $r[0]
        $name = $r[1]
        $type = $r[2]
        $notnull = $r[3]
        $dflt = $r[4]
        $pk = $r[5]
        Write-Host "  $cid | $name | $type | notnull=$notnull | default=$dflt | pk=$pk"
    }
    $r.Close()
}

$conn.Close()
