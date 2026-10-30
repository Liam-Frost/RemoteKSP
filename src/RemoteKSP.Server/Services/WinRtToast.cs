using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace RemoteKSP.Server.Services;

/// <summary>
/// 直接调用 Windows.UI.Notifications 显示通知。不引入 WinRT 投影（目标框架要改成 windows10.0.x，
/// 单文件 exe 会多出约 25 MB），只用到 6 个接口方法。IID 和虚表顺序取自 Windows SDK 的
/// windows.ui.notifications.h / windows.data.xml.dom.h：IInspectable 占前 6 个槽位
/// </summary>
internal static unsafe partial class WinRtToast
{
    private static readonly Guid IID_IXmlDocument = new("f7f3a506-1e87-42d6-bcfb-b8c809fa5494");
    private static readonly Guid IID_IXmlDocumentIO = new("6cd0e74e-ee65-4489-9ebf-ca43e87ba637");
    private static readonly Guid IID_IToastNotificationFactory = new("04124b20-82c6-4229-b109-fd9ed4662b53");
    private static readonly Guid IID_IToastNotification2 = new("9dfb9fd1-143a-490e-90bf-b9fba7132de7");
    private static readonly Guid IID_IToastNotificationManagerStatics = new("50ac103f-d235-4598-bbef-98fe4d1a3ad4");

    [LibraryImport("combase.dll")]
    private static partial int RoInitialize(int initType);

    [LibraryImport("combase.dll")]
    private static partial int RoActivateInstance(IntPtr activatableClassId, out IntPtr instance);

    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(IntPtr activatableClassId, in Guid iid, out IntPtr factory);

    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(IntPtr hstring);

    public static void Show(string appUserModelId, string xml, string tag)
    {
        // RPC_E_CHANGED_MODE：线程已经是别的套间，照样可以用
        var init = RoInitialize(0);
        if (init < 0 && init != unchecked((int)0x80010106))
            Marshal.ThrowExceptionForHR(init);

        var owned = new List<IntPtr>();
        var strings = new List<IntPtr>();
        IntPtr Str(string s) { Check(WindowsCreateString(s, s.Length, out var h)); strings.Add(h); return h; }
        IntPtr Own(IntPtr p) { owned.Add(p); return p; }
        try
        {
            Check(RoActivateInstance(Str("Windows.Data.Xml.Dom.XmlDocument"), out var document));
            Own(document);
            var documentIO = Own(Query(document, IID_IXmlDocumentIO));
            Check(Call(documentIO, 6, Str(xml)));                                   // IXmlDocumentIO.LoadXml
            var xmlDocument = Own(Query(document, IID_IXmlDocument));

            Check(RoGetActivationFactory(Str("Windows.UI.Notifications.ToastNotification"),
                IID_IToastNotificationFactory, out var factory));
            Own(factory);
            IntPtr toast;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(factory, 6))(factory, xmlDocument, &toast));
            Own(toast);                                                             // CreateToastNotification
            var toast2 = Own(Query(toast, IID_IToastNotification2));
            Check(Call(toast2, 6, Str(tag)));                                      // put_Tag
            Check(Call(toast2, 8, Str(appUserModelId)));                           // put_Group

            Check(RoGetActivationFactory(Str("Windows.UI.Notifications.ToastNotificationManager"),
                IID_IToastNotificationManagerStatics, out var statics));
            Own(statics);
            IntPtr notifier;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(statics, 7))(
                statics, Str(appUserModelId), &notifier));                         // CreateToastNotifierWithId
            Own(notifier);
            Check(Call(notifier, 6, toast));                                       // IToastNotifier.Show
        }
        finally
        {
            for (var i = owned.Count - 1; i >= 0; i--)
                ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(owned[i], 2))(owned[i]);   // Release
            foreach (var h in strings)
                WindowsDeleteString(h);
        }
    }

    private static void* Slot(IntPtr instance, int index) => (*(void***)instance)[index];

    private static int Call(IntPtr instance, int slot, IntPtr arg) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(instance, slot))(instance, arg);

    private static IntPtr Query(IntPtr instance, Guid iid)
    {
        IntPtr result;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(instance, 0))(instance, &iid, &result));
        return result;
    }

    private static void Check(int hr)
    {
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);
    }
}
