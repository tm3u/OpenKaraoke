using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OpenKaraoke.Scoring;

public static class CudaLibraries
{
    private static readonly object Gate=new();
    private static readonly List<nint> Handles=[];
    public static string DirectoryPath {get;set;}=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"OpenKaraoke","ai","python","Lib","site-packages","torch","lib");
    public static bool Available=>File.Exists(Path.Combine(DirectoryPath,"cudnn64_9.dll"));
    internal static void Load()
    {
        lock(Gate)
        {
            if(!Available)throw new FileNotFoundException("尚未安裝本機 AI 的 CUDA 元件。");
            if(Handles.Count>0)return;
            string[] names=["cudart64_12.dll","cublasLt64_12.dll","cublas64_12.dll","cufft64_11.dll","cudnn_graph64_9.dll","cudnn_ops64_9.dll","cudnn_cnn64_9.dll","cudnn_heuristic64_9.dll","cudnn_engines_precompiled64_9.dll","cudnn_engines_runtime_compiled64_9.dll","cudnn_adv64_9.dll","cudnn64_9.dll"];
            var loaded=new List<nint>();
            try
            {
                foreach(var name in names)
                {
                    string path=Path.Combine(DirectoryPath,name);if(!File.Exists(path))throw new FileNotFoundException("尚未安裝本機 AI 的 CUDA 元件。",path);
                    nint handle=LoadLibraryEx(path,0,8);if(handle==0)throw new Win32Exception(Marshal.GetLastWin32Error(),"無法載入 CUDA 元件："+name);loaded.Add(handle);
                }
                Handles.AddRange(loaded);
            }
            catch{foreach(var handle in loaded)FreeLibrary(handle);throw;}
        }
    }
    [DllImport("kernel32.dll",EntryPoint="LoadLibraryExW",CharSet=CharSet.Unicode,SetLastError=true)]private static extern nint LoadLibraryEx(string file,nint reserved,uint flags);
    [DllImport("kernel32.dll")]private static extern bool FreeLibrary(nint handle);
}
