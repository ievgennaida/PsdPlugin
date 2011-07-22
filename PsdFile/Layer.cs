﻿/////////////////////////////////////////////////////////////////////////////////
// Copyright (C) 2006, Frank Blumenberg
// 
// See License.txt for complete licensing and attribution information.
// Permission is hereby granted, free of charge, to any person obtaining a copy 
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell 
// copies of the Software, and to permit persons to whom the Software is 
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR 
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, 
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE 
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER 
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN 
// THE SOFTWARE.
// 
/////////////////////////////////////////////////////////////////////////////////

/////////////////////////////////////////////////////////////////////////////////
//
// This code contains code from the Endogine sprite engine by Jonas Beckeman.
// http://www.endogine.com/CS/
//
/////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;

namespace PhotoshopFile
{
  public class Layer
  {
    ///////////////////////////////////////////////////////////////////////////

    public class Channel
    {
      private Layer m_layer;
      /// <summary>
      /// The layer to which this channel belongs
      /// </summary>
      public Layer Layer
      {
        get { return m_layer; }
      }


      private short m_id;
      /// <summary>
      /// 0 = red, 1 = green, etc.
      /// -1 = transparency mask
      /// -2 = user supplied layer mask
      /// </summary>
      public short ID
      {
        get { return m_id; }
        set { m_id = value; }
      }

      /// <summary>
      /// Total length of the channel data, including compression headers.
      /// </summary>
      public int Length { get; set; }

      private byte[] m_data;
      /// <summary>
      /// Compressed raw channel data, excluding compression headers.
      /// </summary>
      public byte[] Data
      {
        get { return m_data; }
        set { m_data = value; }
      }

      private byte[] m_imageData;
      /// <summary>
      /// The raw image data from the channel.
      /// </summary>
      public byte[] ImageData
      {
        get { return m_imageData; }
        set { m_imageData = value; }
      }

      private ImageCompression m_imageCompression;
      public ImageCompression ImageCompression
      {
        get { return m_imageCompression; }
        set { m_imageCompression = value; }
      }

      public byte[] RleHeader { get; set; }

      //////////////////////////////////////////////////////////////////

      internal Channel(short id, Layer layer)
      {
        m_id = id;
        m_layer = layer;
        m_layer.Channels.Add(this);
        m_layer.SortedChannels.Add(this.ID, this);
      }

      internal Channel(BinaryReverseReader reader, Layer layer)
      {
        Debug.WriteLine("Channel started at " + reader.BaseStream.Position.ToString(CultureInfo.InvariantCulture));
        
        m_id = reader.ReadInt16();
        Length = reader.ReadInt32();

        m_layer = layer;
      }

      internal void Save(BinaryReverseWriter writer)
      {
        Debug.WriteLine("Channel Save started at " + writer.BaseStream.Position.ToString(CultureInfo.InvariantCulture));

        writer.Write(m_id);
        writer.Write(this.Length);
      }

      //////////////////////////////////////////////////////////////////

      internal void LoadPixelData(BinaryReverseReader reader, Rectangle rect)
      {
        Debug.WriteLine("Channel.LoadPixelData started at " + reader.BaseStream.Position.ToString(CultureInfo.InvariantCulture));

        var endPosition = reader.BaseStream.Position + this.Length;
        m_imageCompression = (ImageCompression)reader.ReadInt16();
        var dataLength = this.Length - 2;

        switch (m_imageCompression)
        {
          case ImageCompression.Raw:
            m_imageData = reader.ReadBytes(dataLength);
            break;
          case ImageCompression.Rle:
            // Discard the RLE row lengths
            reader.ReadBytes(2 * rect.Height);
            var rleDataLength = dataLength - 2 * rect.Height;

            // The PSD specification states that rows are padded to even sizes.
            // However, PSD files generated by Photoshop CS4 do not actually
            // follow this stipulation.
            m_data = reader.ReadBytes(rleDataLength);
            break;
          case ImageCompression.Zip:
          case ImageCompression.ZipPrediction:
            m_data = reader.ReadBytes(dataLength);
            break;
        }

        Debug.Assert(reader.BaseStream.Position == endPosition, "Pixel data successfully read in.");
      }

      public void DecompressImageData(Rectangle rect)
      {
        var bytesPerRow = Util.BytesPerRow(rect, m_layer.PsdFile.Depth);
        var bytesTotal = rect.Height * bytesPerRow;

        if (this.ImageCompression != PhotoshopFile.ImageCompression.Raw)
        {
          m_imageData = new byte[bytesTotal];

          MemoryStream stream = new MemoryStream(m_data);
          switch (this.ImageCompression)
          {
            case PhotoshopFile.ImageCompression.Rle:
              for (int i = 0; i < rect.Height; i++)
              {
                int rowIndex = i * bytesPerRow;
                RleHelper.DecodedRow(stream, m_imageData, rowIndex, bytesPerRow);
              }
              break;

            case PhotoshopFile.ImageCompression.Zip:
            case PhotoshopFile.ImageCompression.ZipPrediction:
              // .NET implements Deflate (RFC 1951) but not zlib (RFC 1950),
              // so we have to skip the first two bytes.
              stream.ReadByte();
              stream.ReadByte();

              var deflateStream = new DeflateStream(stream, CompressionMode.Decompress);
              var bytesDecompressed = deflateStream.Read(m_imageData, 0, bytesTotal);
              Debug.Assert(bytesDecompressed == bytesTotal, "ZIP deflation output is different length than expected.");
              break;
          }
        }

        // Reverse multi-byte pixels to little-endian.  32-bit depth images
        // with ZipPrediction must be left alone because the data is a
        // byte stream.
        bool fReverseEndianness = (m_layer.PsdFile.Depth == 16)
          || (m_layer.PsdFile.Depth == 32) && (this.ImageCompression != PhotoshopFile.ImageCompression.ZipPrediction);
        if (fReverseEndianness)
          ReverseEndianness(rect);

        if (this.ImageCompression == PhotoshopFile.ImageCompression.ZipPrediction)
        {
          UnpredictImageData(rect);
        }
      }

      private void ReverseEndianness(Rectangle rect)
      {
        var byteDepth = Util.BytesFromBitDepth(m_layer.PsdFile.Depth);
        var pixelsTotal = rect.Width * rect.Height;
        if (pixelsTotal == 0)
          return;

        if (byteDepth == 2)
        {
          Util.SwapByteArray2(m_imageData, 0, pixelsTotal);
        }
        else if (byteDepth == 4)
        {
          Util.SwapByteArray4(m_imageData, 0, pixelsTotal);
        }
        else if (byteDepth > 1)
        {
          throw new Exception("Byte-swapping implemented only for 16-bit and 32-bit depths.");
        }
      }


      unsafe private void UnpredictImageData(Rectangle rect)
      {
        if (m_layer.PsdFile.Depth == 16)
        {
          fixed (byte* ptrData = &m_imageData[0])
          {
            for (int iRow = 0; iRow < rect.Height; iRow++)
            {
              UInt16* ptr = (UInt16*)(ptrData + iRow * rect.Width * 2);
              UInt16* ptrEnd = (UInt16*)(ptrData + (iRow + 1) * rect.Width * 2);

              // Start with column 1 of each row
              ptr++;
              while (ptr < ptrEnd)
              {
                *ptr = (UInt16)(*ptr + *(ptr - 1));
                ptr++;
              }
            }
          }
        }
        else if (m_layer.PsdFile.Depth == 32)
        {
          var reorderedData = new byte[m_imageData.Length];
          fixed (byte* ptrData = &m_imageData[0]) 
          {
            // Undo the prediction on the byte stream
            for (int iRow = 0; iRow < rect.Height; iRow++)
            {
              // The rows are predicted individually.
              byte* ptr = ptrData + iRow * rect.Width * 4;
              byte* ptrEnd = ptrData + (iRow + 1) * rect.Width * 4;

              // Start with column 1 of each row
              ptr++;
              while (ptr < ptrEnd)
              {
                *ptr = (byte)(*ptr + *(ptr - 1));
                ptr++;
              }
            }

            // Within each row, the individual bytes of the 32-bit words are
            // packed together, high-order bytes before low-order bytes.
            // We now unpack them into words and reverse to little-endian.
            int offset1 = rect.Width;
            int offset2 = 2 * offset1;
            int offset3 = 3 * offset1;
            fixed (byte* dstPtrData = &reorderedData[0])
            {
              for (int iRow = 0; iRow < rect.Height; iRow++)
              {
                byte* dstPtr = dstPtrData + iRow * rect.Width * 4;
                byte* dstPtrEnd = dstPtrData + (iRow + 1) * rect.Width * 4;

                byte* srcPtr = ptrData + iRow * rect.Width * 4;

                while (dstPtr < dstPtrEnd)
                {
                  *(dstPtr++) = *(srcPtr + offset3);
                  *(dstPtr++) = *(srcPtr + offset2);
                  *(dstPtr++) = *(srcPtr + offset1);
                  *(dstPtr++) = *srcPtr;

                  srcPtr++;
                }
              }
            }
          }

          m_imageData = reorderedData;
        }
        else
        {
          throw new Exception("ZIP prediction is only available for 16 and 32 bit depths.");
        }
      }

      public void CompressImageData()
      {
        if (m_imageCompression == ImageCompression.Rle)
        {
          MemoryStream dataStream = new MemoryStream();
          MemoryStream headerStream = new MemoryStream();
          BinaryReverseWriter headerWriter = new BinaryReverseWriter(headerStream);

          //---------------------------------------------------------------

          short[] rleRowLengths = new short[m_layer.Rect.Height];
          int bytesPerRow = Util.BytesPerRow(m_layer.Rect, m_layer.PsdFile.Depth);
          for (int row = 0; row < m_layer.Rect.Height; row++)
          {
            int rowIndex = row * m_layer.m_rect.Width;
            rleRowLengths[row] = (short)RleHelper.EncodedRow(dataStream, m_imageData, rowIndex, bytesPerRow);
          }

          // Write RLE row lengths and save
          for (int i = 0; i < rleRowLengths.Length; i++)
          {
            headerWriter.Write((short)rleRowLengths[i]);
          }
          headerStream.Flush();
          this.RleHeader = headerStream.ToArray();
          headerStream.Close();

          // Save compressed data
          dataStream.Flush();
          m_data = dataStream.ToArray();
          dataStream.Close();

          this.Length = 2 + this.RleHeader.Length + m_data.Length;
        }
        else
        {
          m_data = m_imageData;
          this.Length = 2 + m_data.Length;
        }


      }

      internal void SavePixelData(BinaryReverseWriter writer)
      {
        Debug.WriteLine("Channel SavePixelData started at " + writer.BaseStream.Position.ToString(CultureInfo.InvariantCulture));

        writer.Write((short)m_imageCompression);
        if (m_imageCompression == PhotoshopFile.ImageCompression.Rle)
          writer.Write(this.RleHeader);
        writer.Write(m_data);
      }

      //////////////////////////////////////////////////////////////////

      public BinaryReverseReader DataReader
      {
        get
        {
          if (m_data == null)
            return null;

          return new BinaryReverseReader(new System.IO.MemoryStream(this.m_data));
        }
      }
    }

    ///////////////////////////////////////////////////////////////////////////

    public class Mask
    {
      private Layer m_layer;
      /// <summary>
      /// The layer to which this mask belongs
      /// </summary>
      public Layer Layer
      {
        get { return m_layer; }
      }

      private Rectangle m_rect = Rectangle.Empty;
      /// <summary>
      /// The rectangle enclosing the mask.
      /// </summary>
      public Rectangle Rect
      {
        get { return m_rect; }
        set { m_rect = value; }
      }

      private byte m_defaultColor;
      public byte DefaultColor
      {
        get { return m_defaultColor; }
        set { m_defaultColor = value; }
      }


      private static int positionIsRelativeBit = BitVector32.CreateMask();
      private static int disabledBit = BitVector32.CreateMask(positionIsRelativeBit);
      private static int invertOnBlendBit = BitVector32.CreateMask(disabledBit);

      private BitVector32 m_flags = new BitVector32();
      /// <summary>
      /// If true, the position of the mask is relative to the layer.
      /// </summary>
      public bool PositionIsRelative
      {
        get
        {
          return m_flags[positionIsRelativeBit];
        }
        set
        {
          m_flags[positionIsRelativeBit] = value;
        }
      }

      public bool Disabled
      {
        get { return m_flags[disabledBit]; }
        set { m_flags[disabledBit] = value; }
      }

      /// <summary>
      /// if true, invert the mask when blending.
      /// </summary>
      public bool InvertOnBlendBit
      {
        get { return m_flags[invertOnBlendBit]; }
        set { m_flags[invertOnBlendBit] = value; }
      }

      ///////////////////////////////////////////////////////////////////////////

      internal Mask(Layer layer)
      {
        m_layer = layer;
        m_layer.MaskData = this;
      }

      ///////////////////////////////////////////////////////////////////////////

      internal Mask(BinaryReverseReader reader, Layer layer)
      {
        Debug.WriteLine("Mask started at " + reader.BaseStream.Position.ToString(CultureInfo.InvariantCulture));

        m_layer = layer;

        uint maskLength = reader.ReadUInt32();

        if (maskLength <= 0)
          return;

        long startPosition = reader.BaseStream.Position;

        //-----------------------------------------------------------------------

        m_rect = new Rectangle();
        m_rect.Y = reader.ReadInt32();
        m_rect.X = reader.ReadInt32();
        m_rect.Height = reader.ReadInt32() - m_rect.Y;
        m_rect.Width = reader.ReadInt32() - m_rect.X;

        m_defaultColor = reader.ReadByte();

        //-----------------------------------------------------------------------

        byte flags = reader.ReadByte();
        m_flags = new BitVector32(flags);

        //-----------------------------------------------------------------------

        if (maskLength == 36)
        {
          BitVector32 realFlags = new BitVector32(reader.ReadByte());

          byte realUserMaskBackground = reader.ReadByte();

          Rectangle rect = new Rectangle();
          rect.Y = reader.ReadInt32();
          rect.X = reader.ReadInt32();
          rect.Height = reader.ReadInt32() - m_rect.Y;
          rect.Width = reader.ReadInt32() - m_rect.X;
        }


        // there is other stuff following, but we will ignore this.
        reader.BaseStream.Position = startPosition + maskLength;
      }

      ///////////////////////////////////////////////////////////////////////////

      public void Save(BinaryReverseWriter writer)
      {
        Debug.WriteLine("Mask Save started at " + writer.BaseStream.Position.ToString(CultureInfo.InvariantCulture));

        if (m_rect.IsEmpty)
        {
          writer.Write((uint)0);
          return;
        }

        using (new LengthWriter(writer))
        {
          writer.Write(m_rect.Top);
          writer.Write(m_rect.Left);
          writer.Write(m_rect.Bottom);
          writer.Write(m_rect.Right);

          writer.Write(m_defaultColor);

          writer.Write((byte)m_flags.Data);

          // padding 2 bytes so that size is 20
          writer.Write((int)0);
        }
      }

      //////////////////////////////////////////////////////////////////

      /// <summary>
      /// The raw image data from the channel.
      /// </summary>
      public byte[] m_imageData;

      public byte[] ImageData
      {
        get { return m_imageData; }
        set { m_imageData = value; }
      }

    }


    ///////////////////////////////////////////////////////////////////////////

    public class BlendingRanges
    {
      private Layer m_layer;
      /// <summary>
      /// The layer to which this channel belongs
      /// </summary>
      public Layer Layer
      {
        get { return m_layer; }
      }

      private byte[] m_data = new byte[0];

      public byte[] Data
      {
        get { return m_data; }
        set { m_data = value; }
      }

      ///////////////////////////////////////////////////////////////////////////

      public BlendingRanges(Layer layer)
      {
        m_layer = layer;
        m_layer.BlendingRangesData = this;
      }

      ///////////////////////////////////////////////////////////////////////////

      public BlendingRanges(BinaryReverseReader reader, Layer layer)
      {
        Debug.WriteLine("BlendingRanges started at " + reader.BaseStream.Position.ToString(CultureInfo.InvariantCulture));

        m_layer = layer;
        int dataLength = reader.ReadInt32();
        if (dataLength <= 0)
          return;

        m_data = reader.ReadBytes(dataLength);
      }

      ///////////////////////////////////////////////////////////////////////////

      public void Save(BinaryReverseWriter writer)
      {
        Debug.WriteLine("BlendingRanges Save started at " + writer.BaseStream.Position.ToString(CultureInfo.InvariantCulture));

        writer.Write((uint)m_data.Length);
        writer.Write(m_data);
      }
    }

    ///////////////////////////////////////////////////////////////////////////

    public class AdjustmentLayerInfo
    {
      private Layer m_layer;

      private string m_key;
      public string Key
      {
        get { return m_key; }
        set { m_key = value; }
      }

      private byte[] m_data;
      public byte[] Data
      {
        get { return m_data; }
        set { m_data = value; }
      }

      public AdjustmentLayerInfo(string key, Layer layer)
      {
        m_key = key;
        m_layer = layer;
        m_layer.AdjustmentInfo.Add(this);
      }

      public AdjustmentLayerInfo(BinaryReverseReader reader, Layer layer)
      {
        Debug.WriteLine("AdjustmentLayerInfo started at " + reader.BaseStream.Position.ToString(CultureInfo.InvariantCulture));

        m_layer = layer;

        string signature = new string(reader.ReadChars(4));
        if (signature != "8BIM")
        {
          throw new IOException("Could not read an image resource");
        }

        m_key = new string(reader.ReadChars(4));
        uint dataLength = reader.ReadUInt32();
        m_data = reader.ReadBytes((int)dataLength);
      }

      public void Save(BinaryReverseWriter writer)
      {
        Debug.WriteLine("AdjustmentLayerInfo Save started at " + writer.BaseStream.Position.ToString(CultureInfo.InvariantCulture));

        string signature = "8BIM";

        writer.Write(signature.ToCharArray());
        writer.Write(m_key.ToCharArray());
        writer.Write((uint)m_data.Length);
        writer.Write(m_data);
      }

      //////////////////////////////////////////////////////////////////

      public BinaryReverseReader DataReader
      {
        get
        {
          return new BinaryReverseReader(new System.IO.MemoryStream(this.m_data));
        }
      }
    }


    ///////////////////////////////////////////////////////////////////////////

    private PsdFile m_psdFile;
    internal PsdFile PsdFile
    {
      get { return m_psdFile; }
    }

    private Rectangle m_rect = Rectangle.Empty;
    /// <summary>
    /// The rectangle containing the contents of the layer.
    /// </summary>
    public Rectangle Rect
    {
      get { return m_rect; }
      set { m_rect = value; }
    }


    /// <summary>
    /// Channel information.
    /// </summary>
    private List<Channel> m_channels = new List<Channel>();

    public List<Channel> Channels
    {
      get { return m_channels; }
    }

    /// <summary>
    /// Returns channels with nonnegative IDs as an array, for faster indexing.
    /// </summary>
    public Channel[] ChannelsArray
    {
      get
      {
        short maxChannelId = -1;
        foreach (short channelId in SortedChannels.Keys)
        {
          if (channelId > maxChannelId)
            maxChannelId = channelId;
        }

        Channel[] channelsArray = new Channel[maxChannelId + 1];
        for (short i=0; i <= maxChannelId; i++)
        {
          if (SortedChannels.ContainsKey(i))
            channelsArray[i] = SortedChannels[i];
        }
        return channelsArray;
      }
    }

    /// <summary>
    /// Returns alpha channel if it exists, otherwise null.
    /// </summary>
    public Channel AlphaChannel
    {
      get
      {
        if (SortedChannels.ContainsKey(-1))
          return SortedChannels[-1];
        else
          return null;
      }
    }

    private SortedList<short, Channel> m_sortedChannels = new SortedList<short, Channel>();
    public SortedList<short, Channel> SortedChannels
    {
      get
      {
        return m_sortedChannels;
      }
    }

    private string m_blendModeKey="norm";
    /// <summary>
    /// The blend mode key for the layer
    /// </summary>
    /// <remarks>
    /// <list type="table">
    /// </item>
    /// <term>norm</term><description>normal</description>
    /// <term>dark</term><description>darken</description>
    /// <term>lite</term><description>lighten</description>
    /// <term>hue </term><description>hue</description>
    /// <term>sat </term><description>saturation</description>
    /// <term>colr</term><description>color</description>
    /// <term>lum </term><description>luminosity</description>
    /// <term>mul </term><description>multiply</description>
    /// <term>scrn</term><description>screen</description>
    /// <term>diss</term><description>dissolve</description>
    /// <term>over</term><description>overlay</description>
    /// <term>hLit</term><description>hard light</description>
    /// <term>sLit</term><description>soft light</description>
    /// <term>diff</term><description>difference</description>
    /// <term>smud</term><description>exlusion</description>
    /// <term>div </term><description>color dodge</description>
    /// <term>idiv</term><description>color burn</description>
    /// </list>
    /// </remarks>
    public string BlendModeKey
    {
      get { return m_blendModeKey; }
      set
      {
        if (value.Length != 4) throw new ArgumentException("Key length must be 4");
        m_blendModeKey = value;
      }
    }


    private byte m_opacity;
    /// <summary>
    /// 0 = transparent ... 255 = opaque
    /// </summary>
    public byte Opacity
    {
      get { return m_opacity; }
      set { m_opacity = value; }
    }


    private bool m_clipping;
    /// <summary>
    /// false = base, true = non-base
    /// </summary>
    public bool Clipping
    {
      get { return m_clipping; }
      set { m_clipping = value; }
    }

    private static int protectTransBit = BitVector32.CreateMask();
    private static int visibleBit = BitVector32.CreateMask(protectTransBit);

    BitVector32 m_flags = new BitVector32();

    /// <summary>
    /// If true, the layer is visible.
    /// </summary>
    public bool Visible
    {
      get { return !m_flags[visibleBit]; }
      set { m_flags[visibleBit] = !value; }
    }


    /// <summary>
    /// Protect the transparency
    /// </summary>
    public bool ProtectTrans
    {
      get { return m_flags[protectTransBit]; }
      set { m_flags[protectTransBit] = value; }
    }


    private string m_name;
    /// <summary>
    /// The descriptive layer name
    /// </summary>
    public string Name
    {
      get { return m_name; }
      set { m_name = value; }
    }

    private BlendingRanges m_blendingRangesData;
    public Layer.BlendingRanges BlendingRangesData
    {
      get { return m_blendingRangesData; }
      set { m_blendingRangesData = value; }
    }

    private Mask m_maskData;
    public Layer.Mask MaskData
    {
      get { return m_maskData; }
      set { m_maskData = value; }
    }

    private List<AdjustmentLayerInfo> m_adjustmentInfo = new List<AdjustmentLayerInfo>();
    public List<Layer.AdjustmentLayerInfo> AdjustmentInfo
    {
      get { return m_adjustmentInfo; }
      set { m_adjustmentInfo = value; }
    }

    ///////////////////////////////////////////////////////////////////////////

    public Layer(PsdFile psdFile)
    {
      m_psdFile = psdFile;
      m_psdFile.Layers.Add(this);
    }

    public Layer(BinaryReverseReader reader, PsdFile psdFile)
    {
      Debug.WriteLine("Layer started at " + reader.BaseStream.Position.ToString(CultureInfo.InvariantCulture));

      m_psdFile = psdFile;
      m_rect = new Rectangle();
      m_rect.Y = reader.ReadInt32();
      m_rect.X = reader.ReadInt32();
      m_rect.Height = reader.ReadInt32() - m_rect.Y;
      m_rect.Width = reader.ReadInt32() - m_rect.X;

      //-----------------------------------------------------------------------

      int numberOfChannels = reader.ReadUInt16();
      this.m_channels.Clear();
      for (int channel = 0; channel < numberOfChannels; channel++)
      {
        Channel ch = new Channel(reader, this);
        m_channels.Add(ch);
        m_sortedChannels.Add(ch.ID, ch);
      }

      //-----------------------------------------------------------------------

      string signature = new string(reader.ReadChars(4));
      if (signature != "8BIM")
        throw (new IOException("Layer ChannelHeader error!"));

      m_blendModeKey = new string(reader.ReadChars(4));
      m_opacity = reader.ReadByte();

      m_clipping = reader.ReadByte() > 0;

      //-----------------------------------------------------------------------

      byte flags = reader.ReadByte();
      m_flags = new BitVector32(flags);

      //-----------------------------------------------------------------------

      reader.ReadByte(); //padding

      //-----------------------------------------------------------------------

      Debug.WriteLine("Layer extraDataSize started at " + reader.BaseStream.Position.ToString(CultureInfo.InvariantCulture));

      // this is the total size of the MaskData, the BlendingRangesData, the 
      // Name and the AdjustmentLayerInfo
      uint extraDataSize = reader.ReadUInt32();

      // remember the start position for calculation of the 
      // AdjustmentLayerInfo size
      long extraDataStartPosition = reader.BaseStream.Position;

      m_maskData = new Mask(reader, this);
      m_blendingRangesData = new BlendingRanges(reader, this);

      //-----------------------------------------------------------------------

      long namePosition = reader.BaseStream.Position;

      m_name = reader.ReadPascalString();

      int paddingBytes = (int)((reader.BaseStream.Position - namePosition) % 4);

      Debug.Print("Layer {0} padding bytes after name", paddingBytes);
      reader.ReadBytes(paddingBytes);

      //-----------------------------------------------------------------------
      // Process Additional Layer Information

      m_adjustmentInfo.Clear();

      long adjustmentLayerEndPos = extraDataStartPosition + extraDataSize;
      while (reader.BaseStream.Position < adjustmentLayerEndPos)
      {
        try
        {
          m_adjustmentInfo.Add(new AdjustmentLayerInfo(reader, this));
        }
        catch
        {
          reader.BaseStream.Position = adjustmentLayerEndPos;
        }
      }

      foreach (var adjustmentInfo in m_adjustmentInfo)
      {
        switch (adjustmentInfo.Key)
        {
          case "luni":
            var length = Util.GetBigEndianInt32(adjustmentInfo.Data, 0);
            m_name = Encoding.BigEndianUnicode.GetString(adjustmentInfo.Data, 4, length * 2);
            break;
        }
      }

      //-----------------------------------------------------------------------
      // make sure we are not on a wrong offset, so set the stream position 
      // manually
      reader.BaseStream.Position = adjustmentLayerEndPos;
    }

    ///////////////////////////////////////////////////////////////////////////

    public void PrepareSave(PaintDotNet.Threading.PrivateThreadPool threadPool)
    {
      foreach (Channel ch in m_channels)
      {
        CompressChannelContext ccc = new CompressChannelContext(ch);
        WaitCallback waitCallback = new WaitCallback(ccc.CompressChannel);
        threadPool.QueueUserWorkItem(waitCallback);
      }
    }

    public void Save(BinaryReverseWriter writer)
    {
      Debug.WriteLine("Layer Save started at " + writer.BaseStream.Position.ToString(CultureInfo.InvariantCulture));

      writer.Write(m_rect.Top);
      writer.Write(m_rect.Left);
      writer.Write(m_rect.Bottom);
      writer.Write(m_rect.Right);

      //-----------------------------------------------------------------------

      writer.Write((short)m_channels.Count);
      foreach (Channel ch in m_channels)
        ch.Save(writer);

      //-----------------------------------------------------------------------

      string signature = "8BIM";
      writer.Write(signature.ToCharArray());
      writer.Write(m_blendModeKey.ToCharArray());
      writer.Write(m_opacity);
      writer.Write((byte)(m_clipping ? 1 : 0));

      writer.Write((byte)m_flags.Data);

      //-----------------------------------------------------------------------

      writer.Write((byte)0);

      //-----------------------------------------------------------------------

      using (new LengthWriter(writer))
      {
        m_maskData.Save(writer);
        m_blendingRangesData.Save(writer);

        long namePosition = writer.BaseStream.Position;

        writer.WritePascalString(m_name);

        int paddingBytes = (int)((writer.BaseStream.Position - namePosition) % 4);
        Debug.Print("Layer {0} write padding bytes after name", paddingBytes);

        for (int i = 0; i < paddingBytes;i++ )
          writer.Write((byte)0);

        foreach (AdjustmentLayerInfo info in m_adjustmentInfo)
        {
          info.Save(writer);
        }
      }
    }

    private class CompressChannelContext
    {
      private PhotoshopFile.Layer.Channel ch;

      public CompressChannelContext(PhotoshopFile.Layer.Channel ch)
      {
        this.ch = ch;
      }

      public void CompressChannel(object context)
      {
        ch.CompressImageData();
      }
    }

  }
}
