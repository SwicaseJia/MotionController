﻿using ModbusDll;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace OQC_IC_CHECK_System
{
    abstract class ModbusValue
    {
        #region 值
        /// <summary>
        /// 线圈
        /// </summary>
        internal AllCoil Coils = new AllCoil();
        /// <summary>
        /// 保持寄存器
        /// </summary>
        internal AllHoldingRegister HoldingRegisters = new AllHoldingRegister();

        #endregion

        protected void SetBitDataValue()
        { }

        private void setbitdatavalue(int Index, bool Value)
        {

        }

        protected void SetHoldingValue()
        { }
    }

    /// <summary>
    /// 主Modbus
    /// </summary>
    partial class CModbus : ModbusValue
    {
        private Modbus m_Modbus = null;
        private byte m_SlaveID = 1;

        /// <summary>
        /// 待发送消息队列【线程安全】
        /// </summary>
        private ConcurrentQueue<InputModule> MsgList = new ConcurrentQueue<InputModule>();

        private bool m_CommRun = false;
        /// <summary>
        /// 通讯是否正常运行
        /// </summary>
        internal bool CommRun { get { return m_CommRun; } }

        public delegate void dele_MessageText(string str, Modbus.emMsgType MsgType);//显示消息的委托
        public event dele_MessageText event_MessageText;

        private Logs log = Logs.LogsT();

        internal CModbus() { }
        internal CModbus(string IP)
        {
            try
            {
                m_Modbus = new Modbus(IPAddress.Parse(IP), 502);

                Thread thd = new Thread(Thd_PollMsg);
                thd.IsBackground = true;
                thd.Name = "Modbus轮询消息";
                thd.Start();

                m_Modbus.event_MessageText += new Modbus.dele_MessageText(m_Modbus_event_MessageText);
            }
            catch (Exception ex)
            {
                log.AddERRORLOG("IP地址:" + IP + "\r\nModbus初始化失败:" + ex.Message);
                if (IP.Contains("100"))
                    throw new Exception("连接上料机失败,IP地址:" + IP);
                else if (IP.Contains("200"))
                    throw new Exception("连接下料机失败,IP地址:" + IP);
                else throw new Exception("连接上下料机失败,IP地址:" + IP);
            }
        }

        private void m_Modbus_event_MessageText(string str, Modbus.emMsgType nMsgType)
        {
            //Console.WriteLine(nMsgType.ToString() + "\t" + str);
            //后续可以屏蔽，避免日志过多
            //log.AddCommLOG(nMsgType.ToString() + "\t" + str);

            if (this.event_MessageText != null) this.event_MessageText(str, nMsgType);
        }

        private object WaitSync = new object();//是否有同步消息需要发送

        private void Thd_PollMsg()
        {
            int FirstErr = 0;
            Thread.Sleep(10);
            DateTime LastQuery = DateTime.Now;//记录上次轮询时间，避免长时间不轮询
            while (!GlobalVar.SoftWareShutDown)
            {
                try
                {
                    lock (WaitSync)
                    {
                        if (MsgList.Count == 0 ||
                            (MsgList.Count > 1 && (DateTime.Now - LastQuery).TotalMilliseconds > 500))
                        {
                            Query();//轮询查询
                            LastQuery = DateTime.Now;
                        }
                        else if (MsgList.Count > 0)
                        {
                            // log.AddERRORLOG("有数据需要发送："+MsgList.Count);
                            InputModule msg;
                            if (MsgList.TryDequeue(out msg))
                            {
                                SendListMsg(msg);//发送队列消息
                            }
                            else throw new Exception("当前的取值不成功，队列长度:" + MsgList.Count);
                        }
                        FirstErr = 0;
                        m_CommRun = true;
                    }
                }
                catch (Exception ex)
                {
                    m_CommRun = false;
                    log.AddERRORLOG(string.Format("轮询异常:{0}\r\n{1}", ex.Message, ex.StackTrace));
                    Reset();
                    Thread.Sleep(1000);//异常后暂停一秒
                    Reconnect();//重新连接
                }
                finally
                {
                    Thread.Sleep(200);
                }
            }
        }

        private void Reconnect()
        {
            try
            {
                m_Modbus.Disconnect();
                Thread.Sleep(200);
                m_Modbus.Connect();
            }
            catch (Exception ex)
            {
                log.AddERRORLOG(string.Format("重连异常:{0}\r\n{1}", ex.Message, ex.StackTrace));
            }
        }

        /// <summary>
        /// 断线后 复位线圈
        /// </summary>
        private void Reset()
        {
            /*********与PLC通讯断开时，复位以下线圈***********/
            this.Coils.AllowRun.Value = true;
            this.Coils.ManualToAuto.Value = true;

        }

        /// <summary>
        /// 等待
        /// </summary>
        /// <param name="MillSecond"></param>
        private void Sleep(int MillSecond)
        {
            Thread.Sleep(MillSecond);
        }



        /// <summary>
        /// 轮询 所有的值，并写入当前modbus中
        /// </summary>
        private void Query()
        {
            ReadAllCoils();//读取所有的线圈-位读写

            //int index = this.HoldingRegisters.BoardToPosition.Index;//第4个保持寄存器
            //index += 2;
            //ReadALlHoldingRegister(index);//读取所有的寄存器
        }

        public void ReadCoil()
        {
            try
            {
                InputModule input = new InputModule();
                input.bySlaveID = m_SlaveID;
                input.byFunction = Modbus.byREAD_COIL;
                input.nStartAddr = 0;
                input.nDataLength = 30;
                OutputModule rev = m_Modbus.SendMessage_Sync(input);
                int count = 0;
                for (; ; )
                {
                    if (count > 3)
                        throw new Exception("通信异常,读取自动运行线圈失败！");
                    if ((rev == null) || (rev.byFunction != input.byFunction))
                    {
                        rev = m_Modbus.SendMessage_Sync(input);
                        count++;
                        Thread.Sleep(200);
                    }
                    else break;
                }
                int dataLength = rev.byRecvData[8];//读取到的数据长度
                string binary = string.Empty;
                for (int i = 0; i < dataLength; i++)
                {
                    int Decimal = rev.byRecvData[9 + i];//已经为十进制  --数据开始位置为第9个
                    string temp = Convert.ToString(Decimal, 2).PadLeft(8, '0');//十进制转二进制 
                    binary += MyFunction.StrReverse(temp);
                }
                this.Coils.SetBitDatasValue(binary);//直接修改线圈的值
            }
            catch (Exception ex)
            {
                log.AddERRORLOG("单次读取线圈失败:" + ex.Message);
            }
        }


        private void ReadAllCoils()
        {
            InputModule input = new InputModule();
            input.bySlaveID = m_SlaveID;
            input.byFunction = Modbus.byREAD_COIL;
            input.nStartAddr = 0;
            input.nDataLength = 30;

            OutputModule rev = m_Modbus.SendMessage_Sync(input);
            if ((rev == null) || (rev.byFunction != input.byFunction))
            {
                throw new Exception("通信异常,读取自动运行线圈失败！");
            }
            int dataLength = rev.byRecvData[8];//读取到的数据长度

            string binary = string.Empty;
            for (int i = 0; i < dataLength; i++)
            {
                int Decimal = rev.byRecvData[9 + i];//已经为十进制  --数据开始位置为第9个
                string temp = Convert.ToString(Decimal, 2).PadLeft(8, '0');//十进制转二进制 
                binary += MyFunction.StrReverse(temp);
            }
            this.Coils.SetBitDatasValue(binary);//直接修改线圈的值

        }

        /// <summary>
        /// 读取所有的线圈
        /// </summary>
        /// <param name="mode">位状态读取 1:位状态输出；2:位状态读写</param>
        /// <param name="startAddr">线圈起始位置</param>
        /// <param name="Length">在使用的线圈的总长度</param>
        private void ReadAllCoil(int mode, int startAddr, int Length = 10)
        {
            InputModule input = new InputModule();
            input.bySlaveID = m_SlaveID;
            if (mode == 1)
                input.byFunction = Modbus.byREAD_DISCRETE_INPUTS;
            if (mode == 2)
                input.byFunction = Modbus.byREAD_COIL;

            input.nStartAddr = startAddr;
            input.nDataLength = Length;

            OutputModule rev = m_Modbus.SendMessage_Sync(input);
            if ((rev == null) || (rev.byFunction != input.byFunction))
            {
                throw new Exception("通信异常,读取所有所有线圈失败！");
            }
            this.Coils.AllowRun.Value = rev.byRecvData[9] == 0;

        }

        /// <summary>
        /// 读取所有的保持寄存器
        /// </summary>
        /// <param name="Length">在使用的保持寄存器的总长度</param>
        /// <param name="Plusddr">起始地址加上的地址</param>
        private void ReadALlHoldingRegister(int Length = 10, int Plusddr = 0)
        {
            if (Length < 1 || Length > HoldingRegister.MAXLength) throw new Exception("读取保持寄存器寄存器 长度超出范围");

            InputModule input = new InputModule();
            input.bySlaveID = m_SlaveID;
            input.byFunction = Modbus.byREAD_HOLDING_REG;
            input.nStartAddr = HoldingRegister.PLCStartAddr + Plusddr;
            input.nDataLength = Length; ;

            OutputModule rev = m_Modbus.SendMessage_Sync(input);

            if ((rev == null) || (rev.byFunction != input.byFunction))
            {
                throw new Exception("通讯异常，读取所有保持寄存器失败！" + Plusddr);
            }

            //本软件 保持寄存器的数组，读取时转换
            this.HoldingRegisters.SetRegisterArray(rev.byRecvData, Plusddr);
        }

        /// <summary>
        /// 发送线圈消息-同步
        /// </summary>
        /// <param name="coil">线圈</param>
        /// <param name="Press">是否按下 值【0:False;1:True】</param>
        internal bool CoilMsgSync(Coil coil, bool Press)
        {
            lock (WaitSync)
            {
                try
                {
                    InputModule input = new InputModule();
                    input.bySlaveID = m_SlaveID;
                    input.byFunction = Modbus.byWRITE_SINGLE_COIL;
                    input.nStartAddr = coil.Addr;
                    input.nDataLength = Coil.Size;
                    input.byWriteData = new byte[] { Press ? (byte)255 : (byte)0, 0x00 };

                    return SendListMsg(input);
                }
                catch (Exception ex)
                {
                    log.AddERRORLOG("同步消息发送异常:" + ex.Message);
                    return false;
                }
                finally
                {
                }
            }
        }

        /// <summary>
        /// 增加消息队列
        /// </summary>
        /// <param name="input">待发送消息</param>
        internal void AddMsgList(InputModule input)
        {
            input.bySlaveID = m_SlaveID;

            MsgList.Enqueue(input);
            //  log.AddERRORLOG("发送消息:"+input.byWriteData.ToString());
        }

        /// <summary>
        /// 修改单个线圈的值
        /// </summary>
        /// <param name="coil">线圈</param>
        /// <param name="Press">是否按下 值【0:True;1:False】</param>
        internal void AddMsgList(Coil coil, bool Press)
        {
            if (coil.Value == Press) return;//相等则不修改

            InputModule input = new InputModule();
            input.byFunction = Modbus.byWRITE_SINGLE_COIL;
            input.nStartAddr = coil.Addr;
            input.nDataLength = Coil.Size;
            input.byWriteData = new byte[] { Press ? (byte)0 : (byte)255, 0x00 };

            AddMsgList(input);
        }

        /// <summary>
        /// 修改单个保持寄存器的值
        /// </summary>
        /// <param name="register">保持寄存器</param>
        /// <param name="Value">修改的值（包含小数的值，将数值*100后写入）</param>
        internal void AddMsgList(HoldingRegister register, int Value)
        {
            InputModule input = new InputModule();
            input.nStartAddr = register.Addr;
            input.nDataLength = register.Size;
            if (register.Size > 1)
            {
                input.byFunction = Modbus.byWRITE_MULTI_HOLDING_REG;
                input.byWriteData = ModbusTool.HostToNetOrder32(Value);
            }
            else
            {
                input.byFunction = Modbus.byWRITE_SINGLE_HOLDING_REG;
                input.byWriteData = ModbusTool.HostToNetOrder16((short)Value);
            }

            AddMsgList(input);
        }

        private bool SendListMsg(InputModule input)
        {
            OutputModule rev = m_Modbus.SendMessage_Sync(input);
            if ((rev == null) || (rev.byFunction != input.byFunction))
            {
                throw new Exception("发送消息队列 通信异常！");
            }
            else
            {
                //  log.AddERRORLOG("发送数据成功:" + input.byWriteData.ToString());
                return true;
            }
        }

        public bool SendMsg(Coil coil, bool Press)
        {
            try
            {
                InputModule input = new InputModule();
                input.byFunction = Modbus.byWRITE_SINGLE_COIL;
                input.nStartAddr = coil.Addr;
                input.nDataLength = Coil.Size;
                input.byWriteData = new byte[] { Press ? (byte)0 : (byte)255, 0x00 };
                OutputModule rev = null;
                rev = m_Modbus.SendMessage_Sync(input);
                int count = 0;
                for (; ; )
                {
                    if (rev != null) return true;
                    if ((rev == null) || (rev.byFunction != input.byFunction))
                    {
                        rev = m_Modbus.SendMessage_Sync(input);
                        count++;
                    }
                    if (count > 3) throw new Exception("发送线圈消息通信异常！");
                    Thread.Sleep(200);
                }
            }
            catch (Exception ex)
            {
                log.AddERRORLOG("发送线圈信息异常:" + ex.Message);
                return false;
            }
        }

        public bool SendMsg(HoldingRegister register, int Value)
        {
            try
            {
                InputModule input = new InputModule();
                input.nStartAddr = register.Addr;
                input.nDataLength = register.Size;
                if (register.Size > 1)
                {
                    input.byFunction = Modbus.byWRITE_MULTI_HOLDING_REG;
                    input.byWriteData = ModbusTool.HostToNetOrder32(Value);
                }
                else
                {
                    input.byFunction = Modbus.byWRITE_SINGLE_HOLDING_REG;
                    input.byWriteData = ModbusTool.HostToNetOrder16((short)Value);
                }
                OutputModule rev = null;
                rev = m_Modbus.SendMessage_Sync(input);
                if ((rev == null) || (rev.byFunction != input.byFunction))
                {
                    throw new Exception("发送寄存器消息通信异常！");
                }
                else return true;
            }
            catch (Exception ex)
            {
                log.AddERRORLOG("发送寄存器信息异常:" + ex.Message);
                return false;
            }
        }
    }

    /// <summary>
    /// 线圈
    /// </summary>
    class Coil
    {
        /// <summary>
        /// PLC起始地址
        /// </summary>
        public const int PLCStartAddr = 0;
        /// <summary>
        /// 地址
        /// </summary>
        public readonly int Addr;
        /// <summary>
        /// 序号，通讯协议里的第几位
        /// </summary>
        internal readonly int Index;
        /// <summary>
        /// 长度
        /// </summary>
        public const int Size = 1;
        private bool m_Value;
        /// <summary>
        /// 值【0:True;1:False】
        /// </summary>
        public bool Value
        {
            get { return m_Value; }
            set
            {
                if (this.m_Value != value && this.Event_BitDataValueChanged != null)
                {
                    this.m_Value = value;
                    this.Event_BitDataValueChanged(this.Addr);//更改值后触发
                }
                else this.m_Value = value;
            }
        }

        public delegate void dele_BitDataValueChanged(int Addr);//委托-位 值
        public event dele_BitDataValueChanged Event_BitDataValueChanged;

        public Coil(int addr)
        {
            this.Index = addr - 1;

            this.Addr = this.Index + PLCStartAddr;//加上PLC的modbus的起始地址.

            m_Value = false;
        }
    }

    /// <summary>
    /// 保持寄存器
    /// </summary>
    public struct HoldingRegister  //结构struct是值类型
    {
        /// <summary>
        /// PLC起始地址
        /// </summary>
        public const int PLCStartAddr = 0;
        /// <summary>
        /// 一次读取寄存器的长度的最大值
        /// </summary>
        public const int MAXLength = 125;

        /// <summary>
        /// 地址
        /// </summary>
        public readonly int Addr;
        /// <summary>
        /// 序号，供读取本地值使用
        /// </summary>
        internal readonly int Index;
        /// <summary>
        /// 长度
        /// </summary>
        public readonly int Size;
        /// <summary>
        /// 值
        /// </summary>
        public int Value
        {
            get { return ModbusTool.WordToInt(AllHoldingRegister.RegisterArray, this.Index, this.Size); }
        }

        private static int m_Length = 0;
        /// <summary>
        /// 保持寄存器的总长度
        /// </summary>
        public static int TotalLength { get { return m_Length; } }

        //public delegate void dele_HoldingValue(double RegisterValue);//委托-寄存器 值
        //public event dele_HoldingValue EventHoldingValue;

        public HoldingRegister(int addr, int size)
        {
            this.Index = addr;

            this.Addr = this.Index + PLCStartAddr;//加上PLC的modbus的起始地址

            this.Size = size;

            m_Length += this.Size;//修改总长度
        }

        /// <summary>
        /// 获得原始byte数据
        /// </summary>
        /// <returns></returns>
        internal byte[] GetByte()
        {
            int m_Size = this.Size * 2;
            int m_Addr = this.Index * 2;
            byte[] byCopy = new byte[m_Size];
            for (int i = 0; i < m_Size; i++)
            {
                byCopy[i] = AllHoldingRegister.RegisterArray[m_Addr + i];
            }
            return byCopy;
        }
    }

    /// <summary>
    /// 所有的线圈
    /// </summary>
    class AllCoil
    {
        #region 位 值
        private Coil m_BitData1 = new Coil(1);
        /// <summary>
        /// 作业允许	
        /// </summary>
        internal Coil AllowRun { get { return m_BitData1; } }

        private Coil m_BitData2 = new Coil(2);
        /// <summary>
        /// 准备状态标志	
        /// </summary>
        internal Coil BoardReady { get { return m_BitData2; } }

        private Coil m_BitData3 = new Coil(3);
        /// <summary>
        /// 异常状态标志	
        /// </summary>
        internal Coil BoardException { get { return m_BitData3; } }

        private Coil m_BitData4 = new Coil(4);
        /// <summary>
        /// 手动自动切换	
        /// </summary>
        internal Coil ManualToAuto { get { return m_BitData4; } }

        private Coil m_BitData5 = new Coil(5);
        /// <summary>
        ///主从交互	"0:托盘吸取完成1:托盘未吸取
        /// </summary>
        internal Coil CommitSignal { get { return m_BitData5; } }

        private Coil m_BitData6 = new Coil(6);
        /// <summary>
        /// 参数更新标志	
        /// </summary>
        internal Coil UpdatePara { get { return m_BitData6; } }

        private Coil m_BitData7 = new Coil(7);
        /// <summary>
        /// PC复位托盘升降
        /// </summary>
        internal Coil ResetComplete { get { return m_BitData7; } }

        private Coil m_BitData8 = new Coil(8);
        /// <summary>
        /// 下料机下料
        /// </summary>
        internal Coil AxisMoveDown { get { return m_BitData8; } }
        //预留
        private Coil m_BitData9 = new Coil(9);
        private Coil m_BitData10 = new Coil(10);

        private Coil m_BitData11 = new Coil(11);
        /// <summary>
        /// 急停	
        /// </summary>
        internal Coil EMG { get { return m_BitData11; } }

        private Coil m_BitData12 = new Coil(12);
        /// <summary>
        /// 启动	
        /// </summary>
        internal Coil RunTime { get { return m_BitData12; } }

        private Coil m_BitData13 = new Coil(13);
        /// <summary>
        ///复位	
        /// </summary>
        internal Coil Reset { get { return m_BitData13; } }

        private Coil m_BitData14 = new Coil(14);
        /// <summary>
        /// 门禁	
        /// </summary>
        internal Coil Lock { get { return m_BitData14; } }

        private Coil m_BitData15 = new Coil(15);
        /// <summary>
        /// 原点信号	
        /// </summary>
        internal Coil ORG { get { return m_BitData15; } }

        private Coil m_BitData16 = new Coil(16);
        /// <summary>
        /// 正限位信号	
        /// </summary>
        internal Coil LMT { get { return m_BitData16; } }

        private Coil m_BitData17 = new Coil(17);
        /// <summary>
        /// 托盘垂直检测信号	
        /// </summary>
        internal Coil BoardCheck { get { return m_BitData17; } }

        private Coil m_BitData18 = new Coil(18);
        /// <summary>
        /// 托盘水平检测信号	
        /// </summary>
        internal Coil BoardArrival { get { return m_BitData18; } }

        private Coil m_BitData19 = new Coil(19);
        private Coil m_BitData20 = new Coil(20);

        private Coil m_BitData21 = new Coil(21);
        /// <summary>
        /// 托盘向上点动	
        /// </summary>
        internal Coil BoardUpJOG { get { return m_BitData21; } }

        private Coil m_BitData22 = new Coil(22);
        /// <summary>
        /// 托盘向下点动	
        /// </summary>
        internal Coil BoardUnderJOG { get { return m_BitData22; } }

        private Coil m_BitData23 = new Coil(23);
        /// <summary>
        /// 托盘回原点	
        /// </summary>
        internal Coil BoardToORG { get { return m_BitData23; } }

        private Coil m_BitData24 = new Coil(24);
        /// <summary>
        /// 托盘到达设定位置
        /// </summary>
        internal Coil BoardToPosition { get { return m_BitData24; } }

        private Coil m_BitData25 = new Coil(25);
        /// <summary>
        /// 托盘夹紧	
        /// </summary>
        internal Coil BoardCline { get { return m_BitData25; } }

        private Coil m_BitData26 = new Coil(26);
        private Coil m_BitData27 = new Coil(27);
        private Coil m_BitData28 = new Coil(28);
        private Coil m_BitData29 = new Coil(29);
        private Coil m_BitData30 = new Coil(30);


        #endregion

        /// <summary>
        /// 所有的线圈
        /// </summary>
        internal readonly Coil[] BitDatas;
        /// <summary>
        /// 线圈的长度
        /// </summary>
        internal int Count { get { return BitDatas.Length; } }

        public delegate void dele_CoilValueChanged(int Addr);
        public event dele_CoilValueChanged Event_CoilValueChanged;//值改变时 触发

        public AllCoil()
        {
            BitDatas = new Coil[] {
                m_BitData1,                m_BitData2,                m_BitData3,                m_BitData4,                m_BitData5,
                m_BitData6,                m_BitData7,                m_BitData8,                m_BitData9,                m_BitData10,
                m_BitData11,               m_BitData12,               m_BitData13,               m_BitData14,               m_BitData15,
                m_BitData16,               m_BitData17,               m_BitData18,               m_BitData19,               m_BitData20,
                m_BitData21,               m_BitData22,               m_BitData23,               m_BitData24,               m_BitData25,
                m_BitData26,               m_BitData27,               m_BitData28,               m_BitData29,               m_BitData30
            };

            foreach (Coil coil in BitDatas)
            {
                coil.Event_BitDataValueChanged += new Coil.dele_BitDataValueChanged(coil_Event_BitDataValueChanged);
            }
        }

        private void coil_Event_BitDataValueChanged(int Addr)
        {
            if (this.Event_CoilValueChanged != null) this.Event_CoilValueChanged(Addr);
        }

        /// <summary>
        /// 设置线圈的值
        /// </summary>
        /// <param name="binary">根据该字符串判断线圈的值</param>
        internal void SetBitDatasValue(string binary)
        {
            char Zero = '0';

            for (int i = 0; i < BitDatas.Length; i++)
            {
                BitDatas[i].Value = (binary[i] == Zero);
            }
        }
    }

    /// <summary>
    /// 所有的保持寄存器
    /// </summary>
    class AllHoldingRegister
    {
        #region 寄存器 值
        private HoldingRegister m_Register0 = new HoldingRegister(0, 1);
        /// <summary>
        /// 上下料模式设置	
        /// </summary>
        internal HoldingRegister BoardMode { get { return m_Register0; } }

        private HoldingRegister m_Register1 = new HoldingRegister(2, 1);
        /// <summary>
        /// 托盘上下轴速度
        /// </summary>
        internal HoldingRegister BoardSpeed { get { return m_Register1; } }

        private HoldingRegister m_Register2 = new HoldingRegister(4, 1);
        /// <summary>
        /// 加减速	
        /// </summary>
        internal HoldingRegister BoardAcc { get { return m_Register2; } }

        private HoldingRegister m_Register3 = new HoldingRegister(6, 1);
        /// <summary>
        /// 手动设定运动位置	
        /// </summary>
        internal HoldingRegister BoardToPosition { get { return m_Register3; } }
        #endregion

        /// <summary>
        /// 保持寄存器数组，先修改该数组，读取保持寄存器时再转换
        /// </summary>
        private static List<byte> m_RegisterList = new List<byte>();
        private static byte[] m_RegisterArray;
        /// <summary>
        /// 获取保持寄存器数组
        /// </summary>
        internal static byte[] RegisterArray
        {
            get
            {
                try
                {
                    return m_RegisterList.Count < HoldingRegister.TotalLength * 2 ? m_RegisterArray : m_RegisterArray = m_RegisterList.ToArray();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                    return new byte[] { 0 };
                }
            }
        }

        public AllHoldingRegister()
        {
            m_RegisterArray = new byte[HoldingRegister.TotalLength * 2];
        }

        /// <summary>
        /// 设置保持寄存器的数组  读取时再转换
        /// </summary>
        /// <param name="Recv">接收的数据</param>
        /// <param name="PlusAddr">追加的地址</param>
        internal void SetRegisterArray(byte[] Recv, int PlusAddr)
        {
            byte[] TempArray = new byte[Recv.Length - 3 - 2];//有效数据，前三位是头，后两位是校验位
            int RecvLenght = Recv[2];
            Array.Copy(Recv, 3, TempArray, 0, TempArray.Length);

            if (PlusAddr == 0) m_RegisterList.Clear();//起始位置，先清空，再添加
            m_RegisterList.AddRange(TempArray);
        }

    }
}
