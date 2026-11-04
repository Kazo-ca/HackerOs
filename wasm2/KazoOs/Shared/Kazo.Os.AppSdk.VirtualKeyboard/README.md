# Kazo.Os.AppSdk.VirtualKeyboard

A reusable, host-agnostic on-screen keyboard and a keyboard-aware split container. The keyboard
never fills a focused text field and contains no `<input>`/`contenteditable` element anywhere, so
it can never itself trigger a native on-screen keyboard — it only raises typed key events to
whatever target the host wires up.

Wrap the content you want to stay visible above the keyboard in `VirtualKeyboardHost`. The content
region shrinks via ordinary CSS flex layout when the keyboard docks below it:

```razor
@using Kazo.Os.AppSdk.VirtualKeyboard

<VirtualKeyboardHost IsKeyboardVisible="_showKeyboard"
                      Options="_keyboardOptions"
                      OnKeyPressed="HandleKeyAsync">
    <MyTextSurface />
</VirtualKeyboardHost>
```

```csharp
private VirtualKeyboardOptions _keyboardOptions = new() { ShowModifierRow = true };

private Task HandleKeyAsync(VirtualKeyPressedEvent evt)
{
    // Decide what the key means for your own target — e.g. append evt.Text to a buffer,
    // or translate evt.Key into your own domain's key contract.
    return Task.CompletedTask;
}
```

`Kazo.Os.AppSdk.Terminal` consumes this package to drive the Terminal's line-mode input and,
through the existing full-screen key-event pipeline, full-screen commands like `nano` with no
changes to those commands.
