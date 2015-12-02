using System;
using System.Runtime.InteropServices;

namespace Akka.Util
{
    [StructLayout(LayoutKind.Explicit)]
    public struct Result<T>
    {
        [FieldOffset(0)] public readonly bool IsSuccess;
        [FieldOffset(1)] public readonly T Value;
        [FieldOffset(1)] public readonly Exception Exception;

        public Result(T value) : this()
        {
            IsSuccess = true;
            Value = value;
        }
        public Result(Exception exception) : this()
        {
            IsSuccess = false;
            Exception = exception;
        }
    }

    public static class Result
    {
        public static Result<T> Success<T>(T value)
        {
            return new Result<T>(value);
        }

        public static Result<T> Failure<T>(Exception exception)
        {
            return new Result<T>(exception);
        }

        public static Result<T> From<T>(Func<T> func)
        {
            try
            {
                var value = func();
                return new Result<T>(value);
            }
            catch (Exception e)
            {
                return new Result<T>(e);
            }
        }
        
    }
}