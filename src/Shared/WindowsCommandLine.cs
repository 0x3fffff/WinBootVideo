using System.Text;

namespace WinBootVideo.Shared
{
    // Microsoft C runtime command-line quoting; no shell is used.
    public static class WindowsCommandLine
    {
        public static string Quote(string value)
        {
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char ch in value)
            {
                if (ch == '\\') { slashes++; continue; }
                if (ch == '"') { result.Append('\\', slashes * 2 + 1); result.Append(ch); }
                else { result.Append('\\', slashes); result.Append(ch); }
                slashes = 0;
            }
            result.Append('\\', slashes * 2);
            return result.Append('"').ToString();
        }
    }
}
