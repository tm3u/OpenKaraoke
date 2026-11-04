using System.Buffers.Binary;

namespace OpenKaraoke.Phone;

public sealed class PhonePacketReader(int sampleRate)
{
    public const int HeaderSize=32;
    public const uint Magic=0x324d4b4f;
    private uint _sequence;
    private double _frame=-1,_origin=double.NaN;
    public bool Accept(ReadOnlySpan<byte> packet,double arrival,PhoneAudio audio)
    {
        if(packet.Length<=HeaderSize||packet.Length>8192+HeaderSize||(packet.Length-HeaderSize)%2!=0||BinaryPrimitives.ReadUInt32LittleEndian(packet)!=Magic)return false;
        uint sequence=BinaryPrimitives.ReadUInt32LittleEndian(packet[4..]);
        double end=BinaryPrimitives.ReadDoubleLittleEndian(packet[8..]),frame=BinaryPrimitives.ReadDoubleLittleEndian(packet[16..]),uncertainty=BinaryPrimitives.ReadDoubleLittleEndian(packet[24..]);
        if(!double.IsFinite(end)||!double.IsFinite(frame)||!double.IsFinite(uncertainty)||frame<(packet.Length-HeaderSize)/2||frame>9007199254740991||frame!=Math.Truncate(frame)||uncertainty is <0 or >5000||end-arrival*1000>uncertainty+1000)return false;
        if(_frame>=0&&(sequence<=_sequence||frame<=_frame))return false;
        _sequence=sequence;_frame=frame;
        if(arrival*1000-end-uncertainty>60){audio.DiscardStalePacket();return true;}
        double origin=end/1000-frame/sampleRate;
        if(double.IsNaN(_origin)||Math.Abs(origin-_origin)>.03)_origin=origin;
        audio.HasCaptureTimestamps=true;
        return audio.AcceptPcm(packet[HeaderSize..],_origin+frame/sampleRate);
    }
}
