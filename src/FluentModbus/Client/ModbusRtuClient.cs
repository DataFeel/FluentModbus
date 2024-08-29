using System.IO.Ports;

namespace FluentModbus
{
    /// <summary>
    /// A Modbus RTU client.
    /// </summary>
    public partial class ModbusRtuClient : ModbusClient, IDisposable
    {
        #region Field

        //private (IModbusRtuSerialPort Value, bool IsInternal)? _serialPort;
        private IModbusRtuSerialPort _serialPort = default!;
        private ModbusFrameBuffer _frameBuffer = default!;

        public SerialPort SystemPort { get; private set; }


        #endregion

        #region Constructors

        /// <summary>
        /// Creates a new Modbus RTU client for communication with Modbus RTU servers or bridges, routers and gateways for communication with TCP end units.
        /// </summary>
        public ModbusRtuClient(IModbusRtuSerialPort serialPort)
        {
            _serialPort = serialPort;
        }

        public ModbusRtuClient(string portString) : this(new SerialParameters(portString))
        {
        }

        public ModbusRtuClient(SerialParameters serialParams)
        {
            var systemPort = new SerialPort(serialParams.Port)
            {
                BaudRate = serialParams.BaudRate,
                Handshake = serialParams.Handshake,
                Parity = serialParams.Parity,
                StopBits = serialParams.StopBits,
                ReadTimeout = serialParams.ReadTimeout,
                WriteTimeout = serialParams.WriteTimeout
            };
            SystemPort = systemPort;

            systemPort.Open();
            systemPort.BaseStream.Flush();
            systemPort.Close();

            _serialPort = new ModbusRtuSerialPort(systemPort);
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the connection status of the underlying serial port.
        /// </summary>
        public override bool IsConnected => _serialPort.IsOpen;


        #endregion

        #region Methods



        /// <summary>
        /// Connect to the specified <paramref name="port"/>.
        /// </summary>
        /// <param name="port">The COM port to be used, e.g. COM1.</param>
        /// <param name="endianness">Specifies the endianness of the data exchanged with the Modbus server.</param>
        public void Connect(ModbusEndianness endianness = ModbusEndianness.BigEndian)
        {

            SwapBytes =
                BitConverter.IsLittleEndian && endianness == ModbusEndianness.BigEndian ||
                !BitConverter.IsLittleEndian && endianness == ModbusEndianness.LittleEndian;

            _frameBuffer = new ModbusFrameBuffer(256);

            //_serialPort.Close();
            //_serialPort.Open();

            if (!_serialPort.IsOpen)
            {
                _serialPort.Open();
            }

            //if (_serialPort.GetType() == typeof(ModbusRtuSerialPort))
            //{
            //    (_serialPort as ModbusRtuSerialPort).
            //}

            //var dump = _serialPort.ToString();
        }


 

        /// <summary>
        /// Initialize the Modbus TCP client with an externally managed <see cref="IModbusRtuSerialPort"/>.
        /// </summary>
        /// <param name="serialPort">The externally managed <see cref="IModbusRtuSerialPort"/>.</param>
        /// <param name="endianness">Specifies the endianness of the data exchanged with the Modbus server.</param>
        //public void Initialize(IModbusRtuSerialPort serial, ModbusEndianness endianness)
        //{
        //    Initialize(serial, endianness);
        //}


        /// <summary>
        /// Closes the opened COM port and frees all resources.
        /// </summary>
        public void Close()
        {
            if (_serialPort != null)
            {
                _serialPort.Close();
            }
            _frameBuffer?.Dispose();
        }

        ///<inheritdoc/>
        protected override Span<byte> TransceiveFrame(byte unitIdentifier, ModbusFunctionCode functionCode, Action<ExtendedBinaryWriter> extendFrame)
        {
            // WARNING: IF YOU EDIT THIS METHOD, REFLECT ALL CHANGES ALSO IN TransceiveFrameAsync!

            int frameLength;
            byte rawFunctionCode;
            ushort crc;

            // build request
            if (!(0 <= unitIdentifier && unitIdentifier <= 247))
                throw new ModbusException(ErrorMessage.ModbusClient_InvalidUnitIdentifier);

            // special case: broadcast (only for write commands)
            if (unitIdentifier == 0)
            {
                switch (functionCode)
                {
                    case ModbusFunctionCode.WriteMultipleRegisters:
                    case ModbusFunctionCode.WriteSingleCoil:
                    case ModbusFunctionCode.WriteSingleRegister:
                    case ModbusFunctionCode.WriteMultipleCoils:
                    case ModbusFunctionCode.WriteFileRecord:
                    case ModbusFunctionCode.MaskWriteRegister:
                        break;
                    default:
                        throw new ModbusException(ErrorMessage.Modbus_InvalidUseOfBroadcast);
                }
            }

            _frameBuffer.Writer.Seek(0, SeekOrigin.Begin);
            _frameBuffer.Writer.Write(unitIdentifier);                                      // 00     Unit Identifier
            extendFrame(_frameBuffer.Writer);
            frameLength = (int)_frameBuffer.Writer.BaseStream.Position;

            // add CRC
            crc = ModbusUtils.CalculateCRC(_frameBuffer.Buffer.AsMemory()[..frameLength]);
            _frameBuffer.Writer.Write(crc);
            frameLength = (int)_frameBuffer.Writer.BaseStream.Position;

            // send request
            _serialPort.Write(_frameBuffer.Buffer, 0, frameLength);

            // special case: broadcast (only for write commands)
            if (unitIdentifier == 0)
                return _frameBuffer.Buffer.AsSpan(0, 0);

            // wait for and process response
            frameLength = 0;
            _frameBuffer.Reader.BaseStream.Seek(0, SeekOrigin.Begin);

            while (true)
            {
                frameLength += _serialPort.Read(_frameBuffer.Buffer, frameLength, _frameBuffer.Buffer.Length - frameLength);

                if (ModbusUtils.DetectResponseFrame(unitIdentifier, _frameBuffer.Buffer.AsMemory()[..frameLength]))
                {
                    break;
                }
                
                else
                {
                    // reset length because one or more chunks of data were received and written to
                    // the buffer, but no valid Modbus frame could be detected and now the buffer is full
                    if (frameLength == _frameBuffer.Buffer.Length)
                        frameLength = 0;
                }
            }

            _ = _frameBuffer.Reader.ReadByte();
            rawFunctionCode = _frameBuffer.Reader.ReadByte();

            if (rawFunctionCode == (byte)ModbusFunctionCode.Error + (byte)functionCode)
                ProcessError(functionCode, (ModbusExceptionCode)_frameBuffer.Buffer[2]);

            else if (rawFunctionCode != (byte)functionCode)
                throw new ModbusException(ErrorMessage.ModbusClient_InvalidResponseFunctionCode);

            return _frameBuffer.Buffer.AsSpan(1, frameLength - 3);
        }

        #endregion

        #region IDisposable

        private bool _disposedValue;

        /// <inheritdoc />
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    Close();
                }

                _disposedValue = true;
            }
        }

        /// <summary>
        /// Disposes the current instance.
        /// </summary>
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        #endregion
    }

    public readonly struct SerialParameters
    {
        public readonly string Port { get; }
        public readonly int BaudRate { get; } = 115200;
        public readonly Handshake Handshake { get; } = Handshake.None;
        public readonly Parity Parity { get; } = Parity.None;
        public readonly StopBits StopBits { get; } = StopBits.One;
        public readonly int ReadTimeout { get; } = 250;
        public readonly int WriteTimeout { get; } = 250;

        public SerialParameters(string port)
        {
            Port = port;
        }
    }
}
