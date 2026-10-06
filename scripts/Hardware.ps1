$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$cpu = @(Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed)
$gpu = @(Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion, VideoProcessor)
$memory = @(Get-CimInstance Win32_PhysicalMemory | Select-Object Capacity, Speed, ConfiguredClockSpeed)
$os = Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber
$system = Get-CimInstance Win32_ComputerSystem | Select-Object Manufacturer, Model, TotalPhysicalMemory
$board = Get-CimInstance Win32_BaseBoard | Select-Object Manufacturer, Product
@{ Cpu = $cpu; Gpu = $gpu; Memory = $memory; Os = $os; System = $system; Board = $board } | ConvertTo-Json -Depth 6 -Compress
