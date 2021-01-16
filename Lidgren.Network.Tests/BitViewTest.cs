using System.Numerics;
using System.Runtime.CompilerServices;
using Xunit;

namespace Lidgren.Network.Tests
{
    public abstract class BitViewTest<T>
        where T : unmanaged
    {
        public const byte DefaultFillByte = 123;

        public NetBitView<T> CreateView(byte fillValue = DefaultFillByte)
        {
            var view = new NetBitView<T>();
            view = view.Fill(fillValue);
            return view;
        }

        [Fact]
        public void Counts()
        {
            NetBitView<T> view = CreateView();
            Assert.True(view.ByteCount == Unsafe.SizeOf<T>());
            Assert.True(view.Length == Unsafe.SizeOf<T>() * 8);
            Assert.True(view.PopCount == Unsafe.SizeOf<T>() * BitOperations.PopCount(DefaultFillByte));
        }

#pragma warning disable CS1718 // Comparison made to same variable
        [Fact]
        public void Equality()
        {
            NetBitView<T> max = CreateView(255);
            NetBitView<T> min = CreateView(0);

            Assert.True(max.Equals(max));
            Assert.True(max.Equals(max.Value));
            Assert.True(max.Equals((object)max));
            Assert.True(max.Equals((object)max.Value));

            Assert.False(max.Equals(min));
            Assert.False(max.Equals(min.Value));
            Assert.False(max.Equals((object)min));
            Assert.False(max.Equals((object)min.Value));

            Assert.True(max == max);
            Assert.True(max == max.Value);
            Assert.False(max != max);
            Assert.False(max != max.Value);

            Assert.False(min == max);
            Assert.False(min == max.Value);
            Assert.True(min != max);
            Assert.True(min != max.Value);

            Assert.False(max == min);
            Assert.False(max == min.Value);
            Assert.True(max != min);
            Assert.True(max != min.Value);
        }

        [Fact]
        public void Comparison()
        {
            NetBitView<T> max = CreateView(255);
            {
                Assert.True(max <= max);
                Assert.True(max >= max);
                Assert.False(max < max);
                Assert.False(max > max);
            }

            NetBitView<T> min = CreateView(0);
            {
                Assert.True(min <= max);
                Assert.True(max >= min);
                Assert.True(min < max);
                Assert.True(max > min);
            }
        }

        protected void ValueComparison(T min, T max)
        {
            var vmax = new NetBitView<T>(max);
            {
                Assert.True(vmax <= max);
                Assert.True(vmax >= max);
                Assert.False(vmax < max);
                Assert.False(vmax > max);

                Assert.True(max <= vmax);
                Assert.True(max >= vmax);
                Assert.False(max < vmax);
                Assert.False(max > vmax);
            }

            var vmin = new NetBitView<T>(min);
            {
                Assert.True(vmin <= max);
                Assert.True(vmax >= min);
                Assert.True(vmin < max);
                Assert.True(vmax > min);

                Assert.True(min <= vmax);
                Assert.True(max >= vmin);
                Assert.True(min < vmax);
                Assert.True(max > vmin);
            }
        }
#pragma warning restore CS1718
    }
}
