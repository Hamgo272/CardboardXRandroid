# Turns lines from server.js into mouse input on this computer:
#   move <x> <y>   (0..1 across the primary screen)   down   up   wheel <delta>
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class GlassesMouse {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, int data, UIntPtr extra);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

  [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public UIntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Explicit, Size = 40)] struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }
  [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);

  // Any character, in any language, whatever the keyboard layout (KEYEVENTF_UNICODE).
  public static void Type(string text) {
    foreach (char c in text) {
      var down = new INPUT { type = 1, ki = new KEYBDINPUT { wScan = c, dwFlags = 0x0004 } };
      var up = new INPUT { type = 1, ki = new KEYBDINPUT { wScan = c, dwFlags = 0x0004 | 0x0002 } };
      SendInput(2, new[] { down, up }, Marshal.SizeOf(typeof(INPUT)));
    }
  }
  public static void Key(byte vk) { keybd_event(vk, 0, 0, UIntPtr.Zero); keybd_event(vk, 0, 2, UIntPtr.Zero); }
}
"@
# Physical pixels, so the position is right with display scaling (125 %, 150 % ...).
[GlassesMouse]::SetProcessDPIAware() | Out-Null
$w = [GlassesMouse]::GetSystemMetrics(0)
$h = [GlassesMouse]::GetSystemMetrics(1)
$culture = [Globalization.CultureInfo]::InvariantCulture
while ($true) {
  $line = [Console]::In.ReadLine()
  if ($null -eq $line) { break }
  $p = $line.Split(' ')
  switch ($p[0]) {
    'move'  { [GlassesMouse]::SetCursorPos([int]([double]::Parse($p[1], $culture) * $w), [int]([double]::Parse($p[2], $culture) * $h)) | Out-Null }
    'down'  { [GlassesMouse]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero) }
    'up'    { [GlassesMouse]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero) }
    'wheel' { [GlassesMouse]::mouse_event(0x0800, 0, 0, [int]$p[1], [UIntPtr]::Zero) }
    'text'  { [GlassesMouse]::Type([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($p[1]))) }
    'key'   { if ($p[1] -eq 'enter') { [GlassesMouse]::Key(0x0D) } elseif ($p[1] -eq 'back') { [GlassesMouse]::Key(0x08) } }
  }
}
