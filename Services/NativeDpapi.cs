using System.ComponentModel; using System.Runtime.InteropServices;
namespace CodexAccountBar.Services;
internal static class NativeDpapi
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError=true, CharSet=CharSet.Unicode)] private static extern bool CryptProtectData(ref Blob input,string? description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
    [DllImport("crypt32.dll", SetLastError=true)] private static extern bool CryptUnprotectData(ref Blob input,IntPtr description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr value);
    public static byte[] Protect(byte[] value)=>Transform(value,true); public static byte[] Unprotect(byte[] value)=>Transform(value,false);
    private static byte[] Transform(byte[] input,bool protect)
    {
        var ptr=Marshal.AllocHGlobal(input.Length); try { Marshal.Copy(input,0,ptr,input.Length); var source=new Blob{Length=input.Length,Data=ptr}; Blob dest;
            var ok=protect?CryptProtectData(ref source,"Codex Account Bar",IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out dest):CryptUnprotectData(ref source,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out dest);
            if(!ok) throw new Win32Exception(Marshal.GetLastWin32Error()); try { var result=new byte[dest.Length]; Marshal.Copy(dest.Data,result,0,result.Length); return result; } finally { LocalFree(dest.Data); }
        } finally { Marshal.FreeHGlobal(ptr); }
    }
}
