using System.Collections.ObjectModel;
using System.Globalization;

namespace DesktopRug.Core;

public enum AppLanguage { English, Persian }
public sealed record UiTranslation(string English, string Persian);

/// <summary>Explicit per-window language; changing it never changes the process or operating-system culture.</summary>
public static class UiText
{
    public static bool IsRtl(AppLanguage language) => language == AppLanguage.Persian;
    public static CultureInfo Culture(AppLanguage language) => CultureInfo.GetCultureInfo(IsRtl(language) ? "fa-IR" : "en-US");

    public static string Get(AppLanguage language, string key, params object[] args)
    {
        ArgumentNullException.ThrowIfNull(key);
        string text = Catalogue.TryGetValue(key, out var pair) ? IsRtl(language) ? pair.Persian : pair.English : key;
        return args.Length == 0 ? text : string.Format(Culture(language), text, args);
    }

    public static string Pattern(AppLanguage language, RugPattern value) => Get(language, value switch
    { RugPattern.Tabriz => "Tabriz", RugPattern.Modern => "Modern", RugPattern.Custom => "Custom", _ => "Kashan" });
    public static string Size(AppLanguage language, RugSize value) => Get(language, value switch
    { RugSize.Small => "Small", RugSize.Large => "Large", RugSize.ExtraLarge => "Extra large", _ => "Medium" });
    public static string Quality(AppLanguage language, SimulationQuality value) => Get(language, value switch
    { SimulationQuality.Economy => "Economy", SimulationQuality.High => "High", _ => "Balanced" });
    public static string PeekMode(AppLanguage language, PeekActivationMode value) => Get(language, value switch
    { PeekActivationMode.Toggle => "Toggle on / off", PeekActivationMode.Hold => "Hold keyboard shortcut", _ => "Timed" });

    // English copy doubles as a stable key. Each declared key always has both language variants.
    public static IReadOnlyDictionary<string, UiTranslation> Catalogue { get; } =
        new ReadOnlyDictionary<string, UiTranslation>(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Desktop Rug"] = "فرش دسکتاپ",
            ["DESKTOP RUG"] = "فرش دسکتاپ",
            ["Desktop Rug · Settings"] = "فرش دسکتاپ · تنظیمات",
            ["A softer desktop."] = "دسکتاپی آرام‌تر.",
            ["A little woven calm. Your files stay exactly where they are."] = "تکه‌ای آرامش بافته‌شده؛ فایل‌هایتان سر جای خود می‌مانند.",
            ["Language"] = "زبان",
            ["Interface language"] = "زبان برنامه",
            ["English"] = "انگلیسی",
            ["Persian"] = "فارسی",
            ["Language changes after saving."] = "زبان برنامه پس از ذخیره تغییر می‌کند.",
            ["Weave & pattern"] = "بافت و نقش",
            ["Weave and pattern"] = "بافت و نقش",
            ["Choose a local image…"] = "انتخاب تصویر از دستگاه…",
            ["Rug size"] = "اندازهٔ فرش",
            ["Simulation"] = "شبیه‌سازی",
            ["Simulation quality"] = "کیفیت شبیه‌سازی",
            ["Size adjustment"] = "تنظیم اندازه",
            ["Rug size adjustment"] = "تنظیم اندازهٔ فرش",
            ["Rotation"] = "چرخش",
            ["Rotation in degrees"] = "زاویهٔ چرخش به درجه",
            ["Soft shadow"] = "سایهٔ نرم",
            ["Weave detail"] = "جزئیات بافت",
            ["Surface lighting"] = "نور سطح فرش",
            ["Display"] = "نمایشگر",
            ["Let real desktop icons gently lift the fabric"] = "برجستگی آیکن‌های دسکتاپ زیر فرش دیده شود",
            ["Quiet fabric sounds"] = "صدای ملایم پارچه",
            ["Launch at Windows sign-in"] = "اجرا هنگام ورود به ویندوز",
            ["Peek at your desktop icons"] = "دیدن آیکن‌های زیر فرش",
            ["Activation"] = "روش فعال‌سازی",
            ["Duration"] = "مدت",
            ["Peek activation mode"] = "روش نمایش آیکن‌های زیر فرش",
            ["Peek duration"] = "مدت نمایش آیکن‌ها",
            ["Keyboard shortcut"] = "میان‌بر صفحه‌کلید",
            ["Ctrl"] = "Ctrl",
            ["Alt"] = "Alt",
            ["Shift"] = "Shift",
            ["Include Ctrl in Peek shortcut"] = "استفاده از Ctrl در میان‌بر نمایش آیکن‌ها",
            ["Include Alt in Peek shortcut"] = "استفاده از Alt در میان‌بر نمایش آیکن‌ها",
            ["Include Shift in Peek shortcut"] = "استفاده از Shift در میان‌بر نمایش آیکن‌ها",
            ["Peek shortcut key"] = "کلید میان‌بر نمایش آیکن‌ها",
            ["Right-click the rug to Peek"] = "نمایش آیکن‌ها با کلیک راست روی فرش",
            ["Use Ctrl or Alt, with an optional Shift. Emergency hide and show / hide shortcuts stay fixed."] = "از Ctrl یا Alt استفاده کنید؛ Shift هم قابل افزودن است. میان‌برهای پنهان‌کردن فوری و نمایش / پنهان‌کردن ثابت می‌مانند.",
            ["Make yourself at home"] = "راحت باشید",
            ["Drag the middle to move across displays. Pull a corner to lift and fold. Scroll to rotate. Double-click to roll or unroll."] = "وسط فرش را بکشید تا بین نمایشگرها جابه‌جا شود. گوشه‌اش را بکشید تا بلند و تا شود. با چرخ ماوس بچرخانید و با دوبار کلیک، لوله یا بازش کنید.",
            ["The rug stays on the desktop beneath your application windows. It sleeps when the fabric settles."] = "فرش روی دسکتاپ و زیر پنجره‌های برنامه‌ها می‌ماند. وقتی حرکتش تمام شود، پردازش آن متوقف می‌شود.",
            ["Hide rug"] = "پنهان‌کردن فرش",
            ["Saving settings"] = "در حال ذخیرهٔ تنظیمات",
            ["Cancel"] = "انصراف",
            ["Save changes"] = "ذخیرهٔ تغییرات",
            ["Saving…"] = "در حال ذخیره…",
            ["{0} sec"] = "{0} ثانیه",
            ["Timed"] = "زمان‌دار",
            ["Toggle on / off"] = "با هر بار فشردن",
            ["Hold keyboard shortcut"] = "با نگه‌داشتن میان‌بر",
            ["Peek stays active until you press the shortcut again or restore the rug from its tray menu."] = "آیکن‌ها نمایان می‌مانند تا دوباره میان‌بر را فشار دهید یا از منوی کنار ساعت، فرش را برگردانید.",
            ["Hold the keyboard shortcut to Peek; release any key to restore. Tray and right-click Peek use the duration above."] = "میان‌بر را نگه دارید تا آیکن‌ها دیده شوند؛ با رهاکردن یکی از کلیدهای آن، فرش برمی‌گردد. کلیک راست و منوی کنار ساعت از مدت بالا استفاده می‌کنند.",
            ["The rug returns after the selected duration. Press the shortcut again to restore it sooner."] = "فرش پس از مدت انتخاب‌شده برمی‌گردد. برای برگشت زودتر، دوباره میان‌بر را فشار دهید.",
            ["Three original procedural designs"] = "سه نقش اختصاصیِ ساخته‌شده در برنامه",
            ["Choose a readable image to use the custom pattern."] = "برای نقش دلخواه، تصویری را انتخاب کنید که قابل خواندن باشد.",
            ["Choose a rug image"] = "انتخاب تصویر فرش",
            ["Images"] = "تصویرها",
            ["Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp"] = "تصویرها|*.png;*.jpg;*.jpeg;*.webp;*.bmp",
            ["Kashan"] = "کاشان",
            ["Tabriz"] = "تبریز",
            ["Modern"] = "مدرن",
            ["Custom"] = "دلخواه",
            ["Small"] = "کوچک",
            ["Medium"] = "متوسط",
            ["Large"] = "بزرگ",
            ["Extra large"] = "خیلی بزرگ",
            ["Economy"] = "کم‌مصرف",
            ["Balanced"] = "متعادل",
            ["High"] = "بالا",
            ["Desktop Rug · Ctrl+Alt+Esc to hide"] = "فرش دسکتاپ · پنهان‌کردن با Ctrl+Alt+Esc",
            ["Show rug"] = "نمایش فرش",
            ["Hide rug    Ctrl+Alt+Esc"] = "پنهان‌کردن فرش    Ctrl+Alt+Esc",
            ["End peek"] = "برگرداندن فرش",
            ["Peek at desktop    {0}"] = "دیدن دسکتاپ    {0}",
            ["Flatten rug"] = "صاف‌کردن فرش",
            ["Restore saved position"] = "بازگشت به جای ذخیره‌شده",
            ["Unroll rug"] = "بازکردن فرش",
            ["Roll up rug"] = "لوله‌کردن فرش",
            ["Pattern"] = "نقش",
            ["Rotate clockwise"] = "چرخش ساعت‌گرد",
            ["Fabric sounds"] = "صدای پارچه",
            ["Icon-aware bumps"] = "برجستگی آیکن‌ها زیر فرش",
            ["Launch at sign-in"] = "اجرا هنگام ورود به ویندوز",
            ["Settings…"] = "تنظیمات…",
            ["Exit Desktop Rug"] = "خروج از فرش دسکتاپ",
            ["Looking for the Windows desktop…"] = "در حال پیدا کردن دسکتاپ ویندوز…",
            ["Ctrl+Alt+Esc is in use. The rug stays hidden until its emergency shortcut is available. Close the conflicting app and relaunch."] = "میان‌بر Ctrl+Alt+Esc در برنامهٔ دیگری استفاده می‌شود. فرش تا آزادشدن میان‌بر پنهان‌کردن فوری، مخفی می‌ماند. آن برنامه را ببندید و فرش دسکتاپ را دوباره اجرا کنید.",
            ["Emergency hide: Ctrl+Alt+Esc."] = "پنهان‌کردن فوری: Ctrl+Alt+Esc.",
            ["Show / hide: Ctrl+Alt+R."] = "نمایش / پنهان‌کردن: Ctrl+Alt+R.",
            ["Ctrl+Alt+R is in use; use the tray."] = "میان‌بر Ctrl+Alt+R در حال استفاده است؛ از منوی کنار ساعت استفاده کنید.",
            ["Peek: {0} ({1})."] = "دیدن آیکن‌ها: {0} ({1}).",
            ["No interactive display is available."] = "هیچ نمایشگری برای کار با برنامه در دسترس نیست.",
            ["No interactive display is currently available."] = "در حال حاضر هیچ نمایشگری برای کار با برنامه در دسترس نیست.",
            ["Custom image unavailable. The original image is unchanged; using Kashan."] = "تصویر دلخواه در دسترس نیست. تصویر اصلی تغییری نکرده است؛ فعلاً نقش کاشان نمایش داده می‌شود.",
            ["The input surface is recovering. Emergency hide and the tray remain available."] = "بخش تعاملی فرش در حال بازیابی است. میان‌بر پنهان‌کردن فوری و منوی کنار ساعت همچنان در دسترس‌اند.",
            ["Choose a valid Peek shortcut with Ctrl or Alt."] = "برای نمایش آیکن‌ها، میان‌بری معتبر با Ctrl یا Alt انتخاب کنید.",
            ["Choose a local image before selecting Custom."] = "پیش از انتخاب نقش دلخواه، تصویری از دستگاه انتخاب کنید.",
            ["Settings saved."] = "تنظیمات ذخیره شد.",
            ["Changes could not be saved or applied. Previous settings were retained. Check the image and local app-data access."] = "ذخیره یا اعمال تغییرات انجام نشد و تنظیمات قبلی حفظ شد. تصویر و دسترسی به پوشهٔ داده‌های برنامه را بررسی کنید.",
            ["Wait for the current settings save before exiting."] = "پیش از خروج، منتظر بمانید تا ذخیرهٔ تنظیمات تمام شود.",
            ["Explorer is slow to respond. The rug remains usable; icon tracking will recover when Explorer responds."] = "پاسخ اکسپلورر با تأخیر همراه است. فرش همچنان کار می‌کند و پس از دریافت پاسخ، موقعیت آیکن‌ها دوباره به‌روز می‌شود.",
            ["Settings could not be saved. Check local app-data access."] = "تنظیمات ذخیره نشد. دسترسی به پوشهٔ داده‌های برنامه را بررسی کنید.",
            ["The display configuration is changing. The rug is hidden while its surfaces recover."] = "چیدمان نمایشگرها در حال تغییر است. فرش تا بازیابی بخش‌های نمایشی پنهان می‌ماند.",
            ["Explorer icon list unavailable; the rug still works without bumps."] = "فهرست آیکن‌های اکسپلورر در دسترس نیست؛ فرش فعلاً بدون برجستگی آیکن‌ها کار می‌کند.",
            ["Desktop icons are hidden. Icon bumps are inactive."] = "آیکن‌های دسکتاپ پنهان‌اند؛ برجستگی آن‌ها زیر فرش غیرفعال است.",
            ["Tracking {0} real desktop icons through Windows accessibility."] = "موقعیت {0} آیکن واقعی دسکتاپ از راه امکانات دسترس‌پذیری ویندوز دنبال می‌شود.",
            ["Explorer accessibility is temporarily unavailable; retrying without icon bumps."] = "دسترسی به اکسپلورر موقتاً ممکن نیست. تلاش دوباره ادامه دارد و فرش فعلاً بدون برجستگی آیکن‌ها کار می‌کند.",
            ["Explorer accessibility timed out."] = "مهلت دریافت پاسخ از امکانات دسترس‌پذیری اکسپلورر تمام شد.",
            ["Icon-aware bumps are turned off."] = "برجستگی آیکن‌ها زیر فرش خاموش است.",
            ["Include Ctrl or Alt in the Peek shortcut. Shift can also be added."] = "میان‌بر نمایش آیکن‌ها باید Ctrl یا Alt داشته باشد. Shift هم قابل افزودن است.",
            ["Ctrl+Alt+Esc and Ctrl+Alt+R are reserved for emergency hide and show / hide."] = "میان‌برهای Ctrl+Alt+Esc و Ctrl+Alt+R برای پنهان‌کردن فوری و نمایش / پنهان‌کردن رزرو شده‌اند.",
            ["F12 is reserved by Windows for the debugger. Choose another key."] = "ویندوز کلید F12 را برای اشکال‌زدایی رزرو کرده است. کلید دیگری انتخاب کنید.",
            ["Choose a letter, number, or function key other than F12."] = "یک حرف یا عدد انگلیسی، یا کلید تابع به‌جز F12 انتخاب کنید.",
            ["Windows could not release an earlier shortcut reservation. Try again or restart Desktop Rug."] = "ویندوز نتوانست میان‌بر قبلی را آزاد کند. دوباره تلاش کنید یا فرش دسکتاپ را از نو اجرا کنید.",
            ["Windows could not replace the shortcut. The previous shortcut remains active."] = "ویندوز نتوانست میان‌بر را جایگزین کند. میان‌بر قبلی همچنان فعال است.",
            ["{0} is already in use. Choose another shortcut; your previous shortcut is unchanged."] = "میان‌بر {0} در حال استفاده است. میان‌بر دیگری انتخاب کنید؛ میان‌بر قبلی شما تغییری نکرده است.",
            ["Windows could not register {0} (error {1}). Your previous shortcut is unchanged."] = "ویندوز نتوانست میان‌بر {0} را ثبت کند (خطای {1}). میان‌بر قبلی شما تغییری نکرده است.",
            ["Desktop Rug could not start. Details are in LocalAppData\\DesktopRug\\desktop-rug.log. Your desktop has not been changed."] = "فرش دسکتاپ اجرا نشد. جزئیات در فایل LocalAppData\\DesktopRug\\desktop-rug.log ثبت شده است. دسکتاپ شما تغییری نکرده است.",
            ["Settings could not be read; safe defaults were restored."] = "تنظیمات خوانده نشد؛ تنظیمات پیش‌فرض بازیابی شد.",
            ["Cannot locate the application executable."] = "فایل اجرایی برنامه پیدا نشد."
        }.ToDictionary(pair => pair.Key, pair => new UiTranslation(pair.Key, pair.Value), StringComparer.Ordinal));
}
