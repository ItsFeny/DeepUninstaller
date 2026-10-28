using System.Text.RegularExpressions;

namespace DeepUninstaller;

public enum AppCategory { Juego, Programa, Desarrollo, Componente, Controlador }

/// <summary>Clasifica cada programa por tipo usando su nombre, fabricante y carpeta.</summary>
public static class Categorizer
{
    private const RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex ComponentName = new(
        @"redistributable|runtime|shared framework|\.net framework|directx|clr types|odbc|ole db|webview2|gameinput|" +
        @"update health|vss writer|help viewer|tools for applications|pc health|setup \(|browser for sql|" +
        @"windows sdk|app installer|visual c\+\+|vcredist|physx|xna framework|openal|bonjour|apple (mobile device|application) support", I);

    private static readonly Regex DriverName = new(
        @"\bdriver\b|controlador|chipset|\bgraphics\b|audio hd|hd audio|ethernet|wi-?fi|bluetooth|wireless lan|" +
        @"serial io|management engine|rapid storage|nvidia|realtek|amd software|radeon", I);

    private static readonly Regex DriverPublisher = new(
        @"^(nvidia|intel|advanced micro devices|amd|realtek|qualcomm|mediatek|broadcom|synaptics|elan)", I);

    private static readonly Regex DevName = new(
        @"\bgit\b|github|node\.?js|python|\bjava\b|\bjdk\b|\bsdk\b|visual studio|vs ?code|android studio|mysql|sql server|" +
        @"postgres|docker|cursor|intellij|pycharm|webstorm|rider|clion|xampp|wamp|postman|unity hub|unreal engine|anaconda|" +
        @"\bnpm\b|yarn|rustup|golang|mongodb|redis|management studio|workbench|\bmpi\b|\bwsl\b|powershell 7|" +
        @"windows terminal|sublime|notepad\+\+|insomnia|dbeaver|heidisql|putty|winscp|filezilla|cmake|\bmsys|cygwin|" +
        @"virtualbox|vmware|wireshark|ngrok|flutter|dart sdk|gradle|maven|composer|php\b|ruby|perl|\blfs\b", I);

    private static readonly Regex DevPublisher = new(
        @"jetbrains|python software|node\.js|git development|github|docker|anysphere|postman|oracle|mysql|" +
        @"the git|microsoft vs code|sublime hq|don ho", I);

    private static readonly Regex GamePublisher = new(
        @"riot games|blizzard|electronic arts|^ea\b|ubisoft|rockstar|activision|valve|epic games|bethesda|cd projekt|" +
        @"^2k|bandai|square enix|mojang|cfx|gaijin|wargaming|hoyoverse|mihoyo|cognosphere|nexon|garena|tencent|krafton|" +
        @"bungie|innersloth|supercell|sega|capcom|konami|warner bros|paradox|devolver|team17|netherrealm|" +
        @"gog\b|overwolf|blitz|lagofast|kakao games|pearl abyss|smilegate|ncsoft|grinding gear|digital extremes", I);

    private static readonly Regex GameName = new(
        @"battle\.net|^steam$|epic games|^ea app|origin|ubisoft connect|uplay|gog galaxy|riot client|league of legends|" +
        @"valorant|minecraft|roblox|fivem|ragemp|alt:v|\bgame\b|games?\b launcher|lagofast|blitz|overwolf|" +
        @"exitlag|noping|wtfast|mortal kombat|fortnite|call of duty|counter-strike|\bgta\b|grand theft|fifa|\bnba 2k|" +
        @"apex legends|rocket league|genshin|honkai|osu!|hearthstone|warcraft|overwatch|diablo|starcraft|destiny", I);

    private static readonly Regex GamePath = new(
        @"\\steamapps\\|\\epic games\\|\\riot games\\|\\gog galaxy\\games|\\ubisoft game launcher\\games|\\ea games\\|" +
        @"\\battle\.net|\\games\\|\\xboxgames\\", I);

    public static AppCategory Classify(InstalledApp a)
    {
        if (a.Source.StartsWith("Juego")) return AppCategory.Juego;

        string name = Regex.Replace(a.Name, @"\(.*?\)", " ");
        string pub = a.Publisher;
        string loc = a.InstallLocation + "\\";

        if (ComponentName.IsMatch(name)) return AppCategory.Componente;
        if (GameName.IsMatch(name) || GamePublisher.IsMatch(pub) || GamePath.IsMatch(loc)) return AppCategory.Juego;
        if (DevName.IsMatch(name) || DevPublisher.IsMatch(pub)) return AppCategory.Desarrollo;
        if (DriverName.IsMatch(name) || DriverPublisher.IsMatch(pub)) return AppCategory.Controlador;
        return AppCategory.Programa;
    }

    public static string Label(AppCategory c) => c switch
    {
        AppCategory.Juego => "Juego",
        AppCategory.Programa => "Programa",
        AppCategory.Desarrollo => "Desarrollo",
        AppCategory.Componente => "Componente del sistema",
        AppCategory.Controlador => "Controlador",
        _ => ""
    };

    /// <summary>Glifo de Material Icons para cada categoría.</summary>
    public static string Glyph(AppCategory c) => c switch
    {
        AppCategory.Juego => Icons.Games,
        AppCategory.Programa => Icons.Desktop,
        AppCategory.Desarrollo => Icons.Code,
        AppCategory.Componente => Icons.Extension,
        AppCategory.Controlador => Icons.Memory,
        _ => Icons.Apps
    };
}
