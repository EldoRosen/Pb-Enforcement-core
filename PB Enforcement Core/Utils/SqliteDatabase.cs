using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Advanced_PB_Limiter.Utils
{
    public sealed class SqliteException : Exception
    {
        public int Code { get; }
        public SqliteException(int code, string message) : base(message) { Code = code; }
    }

    /// <summary>
    /// A very small SQLite client built on winsqlite3.dll, the SQLite library that comes with Windows 10 and Windows Server
    /// 2016 or newer. It needs no extra files next to the plugin.
    /// </summary>
    public sealed class SqliteDatabase : IDisposable
    {
        private const string Library = "winsqlite3.dll";
        private const int Ok = 0;
        private const int Row = 100;
        private const int Done = 101;
        private const int OpenReadOnly = 0x01;
        private const int OpenReadWrite = 0x02;
        private const int OpenCreate = 0x04;
        private static readonly IntPtr Transient = new(-1); // SQLITE_TRANSIENT: SQLite makes its own copy of bound text

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_close(IntPtr db);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int numBytes, out IntPtr statement, IntPtr tail);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_step(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_finalize(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_bind_text(IntPtr statement, int index, byte[] text, int numBytes, IntPtr destructor);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_bytes(IntPtr statement, int column);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_errmsg(IntPtr db);

        private IntPtr _handle;

        private SqliteDatabase(IntPtr handle)
        {
            _handle = handle;
        }

        /// <summary>Opens a database file. With create = false the file must already exist and is only read.</summary>
        public static SqliteDatabase Open(string path, bool create)
        {
            int flags = create ? OpenReadWrite | OpenCreate : OpenReadOnly;
            int result = sqlite3_open_v2(Utf8(path), out IntPtr handle, flags, IntPtr.Zero);
            if (result != Ok)
            {
                string message = handle == IntPtr.Zero ? "SQLite returned code " + result : ReadUtf8(sqlite3_errmsg(handle));
                if (handle != IntPtr.Zero) sqlite3_close(handle);
                throw new SqliteException(result, "Could not open the database: " + message);
            }

            sqlite3_busy_timeout(handle, 5000); // wait up to 5 seconds if another server is writing at the same moment
            return new SqliteDatabase(handle);
        }

        /// <summary>Runs one statement. Values for ?1, ?2 ... are passed as text.</summary>
        public void Execute(string sql, params string[] values)
        {
            using Statement statement = Prepare(sql, values);
            int result = sqlite3_step(statement.Handle);
            while (result == Row) result = sqlite3_step(statement.Handle);
            if (result != Done) throw Error(result);
        }

        /// <summary>Runs a query and returns every row as text columns.</summary>
        public List<string[]> Query(string sql, int columns, params string[] values)
        {
            List<string[]> rows = new();
            using Statement statement = Prepare(sql, values);

            int result;
            while ((result = sqlite3_step(statement.Handle)) == Row)
            {
                string[] row = new string[columns];
                for (int column = 0; column < columns; column++)
                {
                    IntPtr text = sqlite3_column_text(statement.Handle, column);
                    int length = sqlite3_column_bytes(statement.Handle, column);
                    row[column] = text == IntPtr.Zero ? "" : ReadUtf8(text, length);
                }
                rows.Add(row);
            }

            if (result != Done) throw Error(result);
            return rows;
        }

        private Statement Prepare(string sql, string[] values)
        {
            int result = sqlite3_prepare_v2(_handle, Utf8(sql), -1, out IntPtr handle, IntPtr.Zero);
            if (result != Ok) throw Error(result);

            Statement statement = new(handle);
            for (int i = 0; i < values.Length; i++)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(values[i] ?? "");
                int bound = sqlite3_bind_text(handle, i + 1, bytes, bytes.Length, Transient);
                if (bound != Ok)
                {
                    statement.Dispose();
                    throw Error(bound);
                }
            }
            return statement;
        }

        private SqliteException Error(int code) => new(code, ReadUtf8(sqlite3_errmsg(_handle)));

        // SQLite wants zero-terminated UTF-8 text.
        private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text + "\0");

        private static string ReadUtf8(IntPtr pointer, int length)
        {
            if (pointer == IntPtr.Zero || length <= 0) return "";
            byte[] bytes = new byte[length];
            Marshal.Copy(pointer, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }

        private static string ReadUtf8(IntPtr pointer)
        {
            if (pointer == IntPtr.Zero) return "";
            int length = 0;
            while (Marshal.ReadByte(pointer, length) != 0) length++;
            return ReadUtf8(pointer, length);
        }

        public void Dispose()
        {
            if (_handle == IntPtr.Zero) return;
            sqlite3_close(_handle);
            _handle = IntPtr.Zero;
        }

        private sealed class Statement : IDisposable
        {
            public IntPtr Handle { get; private set; }
            public Statement(IntPtr handle) { Handle = handle; }

            public void Dispose()
            {
                if (Handle == IntPtr.Zero) return;
                sqlite3_finalize(Handle);
                Handle = IntPtr.Zero;
            }
        }
    }
}
