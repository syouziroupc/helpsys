param(
  [ValidateSet('Password','Otp','Cookie')]
  [string]$Mode = 'Password'
)

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

$window = New-Object System.Windows.Window
$window.Width = 640
$window.Height = 360
$window.WindowStartupLocation = 'CenterScreen'
$window.Topmost = $true
$window.Title = switch ($Mode) {
  'Password' { 'Account sign in - Password' }
  'Otp' { 'Enter verification code' }
  'Cookie' { 'DevTools - Application - Cookies' }
}

$panel = New-Object System.Windows.Controls.StackPanel
$panel.Margin = '28'

$heading = New-Object System.Windows.Controls.TextBlock
$heading.FontSize = 24
$heading.Margin = '0,0,0,20'
$heading.Text = switch ($Mode) {
  'Password' { 'Sign in to your account' }
  'Otp' { 'Enter verification code' }
  'Cookie' { 'Developer Tools - Application - Cookies' }
}
$panel.Children.Add($heading) | Out-Null

$label = New-Object System.Windows.Controls.TextBlock
$label.Margin = '0,0,0,8'
$label.Text = switch ($Mode) {
  'Password' { 'Password' }
  'Otp' { 'One-time verification code' }
  'Cookie' { 'Session Cookie / Storage values' }
}
$panel.Children.Add($label) | Out-Null

switch ($Mode) {
  'Password' {
    $password = New-Object System.Windows.Controls.PasswordBox
    $password.Name = 'PrivacySmokePassword'
    $password.Width = 360
    $password.Height = 32
    $password.HorizontalAlignment = 'Left'
    $password.Password = 'DO_NOT_SEND_PASSWORD'
    $panel.Children.Add($password) | Out-Null
  }
  'Otp' {
    $otp = New-Object System.Windows.Controls.TextBox
    $otp.Name = 'PrivacySmokeOtp'
    $otp.Width = 360
    $otp.Height = 32
    $otp.HorizontalAlignment = 'Left'
    $otp.Text = '123456'
    $panel.Children.Add($otp) | Out-Null
  }
  'Cookie' {
    $cookie = New-Object System.Windows.Controls.TextBlock
    $cookie.Name = 'PrivacySmokeCookie'
    $cookie.Text = 'session_id=DO_NOT_SEND_COOKIE_VALUE'
    $cookie.Margin = '0,4,0,0'
    $panel.Children.Add($cookie) | Out-Null
  }
}

$note = New-Object System.Windows.Controls.TextBlock
$note.Margin = '0,20,0,0'
$note.Text = 'Privacy smoke test surface'
$panel.Children.Add($note) | Out-Null

$window.Content = $panel
$window.Add_ContentRendered({ $window.Activate() })
$window.ShowDialog() | Out-Null
