using System;
using System.IO;

namespace UnitTests
{
    public class FakeReadStream : Stream
    {
        private long _length;
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public FakeReadStream(long length)
        {
            SetLength(length);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            long toRead = Math.Min(_length - _position, count);
            _position += toRead;
            return (int)toRead;
        }

        public override void SetLength(long value)
        {
            _length = value;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}
