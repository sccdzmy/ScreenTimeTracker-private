namespace ScreenTimeTracker.Native;
public static class Text
{
    public static string Language { get; set; } = "zh-CN";
    public static string Duration(long seconds, bool precise = false)
    {
        seconds = Math.Max(0, seconds);
        long hours = seconds / 3600, minutes = seconds % 3600 / 60, remainder = seconds % 60;
        string value = hours > 0 ? Of($"{hours}小时{minutes}分钟|{hours}h {minutes}m") : minutes > 0 ? Of($"{minutes}分钟|{minutes}m") : Of($"{remainder}秒|{remainder}s");
        return precise && seconds >= 60 && remainder > 0 ? value + Of($"{remainder}秒| {remainder}s") : value;
    }
    public static string Of(string bilingual)
    {
        var values = bilingual.Split('|');
        return Language == "zh-CN" || values.Length == 1 ? values[0] : values[1];
    }
}
