﻿/*
 * AudioDecodingBuffer
 * Buffers up encoded audio packets and provides a constant stream of sound (silence if there is no more audio to decode)
 * This works by having a buffer with N sub-buffers, each of the size of a PCM frame. When Read, this copys the buffer data into the passed array
 * and, if there are no more decoded data, calls Opus to decode the sample
 * 
 * TODO This is decoding audio data on the main thread. We should make decoding happen in a separate thread
 * TODO Use the sequence number in error correcting
 */
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Mumble {
    public class AudioDecodingBuffer
    {
        /// <summary>
        /// How many samples have been decoded
        /// </summary>
        private int _decodedCount;
        /// <summary>
        /// How far along the decoded buffer, in units of numbers of samples,
        /// have currently been read
        /// </summary>
        private int _readingOffset;
        /// <summary>
        /// The index of the next sub-buffer to decode into
        /// </summary>
        private int _nextBufferToDecodeInto;
        private float[][] _decodedBuffer = new float[NumDecodedSubBuffers][];
        private long _nextSequenceToDecode;
        private readonly List<BufferPacket> _encodedBuffer = new List<BufferPacket>();
        private readonly OpusCodec _codec;
        const int NumDecodedSubBuffers = (int)(Constants.MAX_LATENCY_SECONDS * (Constants.SAMPLE_RATE / Constants.FRAME_SIZE));
        const int SubBufferSize = Constants.FRAME_SIZE;

        public AudioDecodingBuffer(OpusCodec codec)
        {
            _codec = codec;
        }
        float[] NextBufferToDecodeInto()
        {
            int nextSubBufferToFill = _nextBufferToDecodeInto++;
            //Make sure we don't go over our max number of buffers
            if (_nextBufferToDecodeInto == NumDecodedSubBuffers)
                _nextBufferToDecodeInto = 0;

            if (_decodedBuffer[nextSubBufferToFill] == null)
                _decodedBuffer[nextSubBufferToFill] = new float[SubBufferSize];

            return _decodedBuffer[nextSubBufferToFill];
        }
        public int Read(float[] buffer, int offset, int count)
        {
            //Debug.Log("We now have " + _encodedBuffer.Count + " encoded packets");
            //Debug.LogWarning("Will read");

            int readCount = 0;
            while (readCount < count)
            {
                if(_decodedCount > 0)
                    readCount += ReadFromBuffer(buffer, offset + readCount, count - readCount);
                else if (!FillBuffer())
                    break;
            }

            //Return silence if there was no data available
            if (readCount == 0)
                Array.Clear(buffer, offset, count);
            return readCount;
        }


        private BufferPacket? GetNextEncodedData()
        {
            if (_encodedBuffer.Count == 0)
                return null;

            int minIndex = 0;
            for (int i = 1; i < _encodedBuffer.Count; i++)
                minIndex = _encodedBuffer[minIndex].Sequence < _encodedBuffer[i].Sequence ? minIndex : i;

            var packet = _encodedBuffer[minIndex];
            _encodedBuffer.RemoveAt(minIndex);

            return packet;
        }

        /// <summary>
        /// Read data that has already been decoded
        /// </summary>
        /// <param name="dst"></param>
        /// <param name="offset"></param>
        /// <param name="count"></param>
        /// <returns></returns>
        private int ReadFromBuffer(float[] dst, int offset, int count)
        {
            int currentBuffer = _readingOffset / SubBufferSize;
            int numDecodedInCurrentBuffer = _decodedCount;// SubBufferSize - _decodedCount % SubBufferSize;
            int currentBufferOffset = SubBufferSize - numDecodedInCurrentBuffer;

            //Copy as much data as we can from the buffer up to the limit
            int readCount = Math.Min(count, numDecodedInCurrentBuffer);
            /*
            Debug.Log("Reading " + readCount
                + " starting at " + currentBufferOffset
                + " starting at overall " + _readingOffset
                + " current buff is " + currentBuffer
                + " into the location " + offset
                + " with in curr buff " + numDecodedInCurrentBuffer
                + " out of " + _decodedBuffer[currentBuffer].Length
                + " with " + _decodedCount);
                */
            if (readCount == 0)
                return 0;

            Array.Copy(_decodedBuffer[currentBuffer], currentBufferOffset, dst, offset, readCount);
            _decodedCount -= readCount;
            _readingOffset += readCount;

            //If we hit the end of the buffer, lap over
            if (_readingOffset == SubBufferSize * NumDecodedSubBuffers)
                _readingOffset = 0;

            return readCount;
        }

        /// <summary>
        /// Decoded data into the buffer
        /// </summary>
        /// <returns></returns>
        private bool FillBuffer()
        {
            var packet = GetNextEncodedData();
            if (!packet.HasValue)
                return false;

            int numRead = _codec.Decode(packet.Value.Data, NextBufferToDecodeInto());
            _decodedCount += numRead;

            _nextSequenceToDecode = packet.Value.Sequence + numRead / Constants.FRAME_SIZE;
            return true;

            ////todo: _nextSequenceToDecode calculation is wrong, which causes this to happen for almost every packet!
            ////Decode a null to indicate a dropped packet
            //if (packet.Value.Sequence != _nextSequenceToDecode)
            //    _codec.Decode(null);
        }
        /// <summary>
        /// Add a new packet of encoded data
        /// </summary>
        /// <param name="sequence">Sequence number of this packet</param>
        /// <param name="data">The encoded audio packet</param>
        /// <param name="codec">The codec to use to decode this packet</param>
        public void AddEncodedPacket(long sequence, byte[] data)
        {
            //If the next seq we expect to decode comes after this packet we've already missed our opportunity!
            if (_nextSequenceToDecode > sequence)
            {
                Debug.LogWarning("Dropping packet number: " + sequence);
                return;
            }

            _encodedBuffer.Add(new BufferPacket
            {
                Data = data,
                Sequence = sequence
            });
        }

        private struct BufferPacket
        {
            public byte[] Data;
            public long Sequence;
        }
    }
}