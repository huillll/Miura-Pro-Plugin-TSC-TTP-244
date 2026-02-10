Option Strict Off
Option Explicit On
Imports System.Data
Class MainWindow
    Shared print_tab As New DataTable

    Private Declare Sub about Lib "tsclib.dll" ()
    Private Declare Sub openport Lib "tsclib.dll" (ByVal PrinterName As String)
    Private Declare Sub closeport Lib "tsclib.dll" ()
    Private Declare Sub sendcommand Lib "tsclib.dll" (ByVal command_Renamed As String)
    Private Declare Sub setup Lib "tsclib.dll" (ByVal LabelWidth As String, ByVal LabelHeight As String, ByVal Speed As String, ByVal Density As String, ByVal Sensor As String, ByVal Vertical As String, ByVal Offset As String)
    Private Declare Sub downloadpcx Lib "tsclib.dll" (ByVal Filename As String, ByVal ImageName As String)
    Private Declare Sub barcode Lib "tsclib.dll" (ByVal X As String, ByVal Y As String, ByVal CodeType As String, ByVal Height_Renamed As String, ByVal Readable As String, ByVal rotation As String, ByVal Narrow As String, ByVal Wide As String, ByVal Code As String)
    Private Declare Sub printerfont Lib "tsclib.dll" (ByVal X As String, ByVal Y As String, ByVal FontName As String, ByVal rotation As String, ByVal Xmul As String, ByVal Ymul As String, ByVal Content As String)
    Private Declare Sub clearbuffer Lib "tsclib.dll" ()
    Private Declare Sub printlabel Lib "tsclib.dll" (ByVal NumberOfSet As String, ByVal NumberOfCopy As String)
    Private Declare Sub formfeed Lib "tsclib.dll" ()
    Private Declare Sub nobackfeed Lib "tsclib.dll" ()
    Private Declare Sub windowsfont Lib "tsclib.dll" (ByVal X As Short, ByVal Y As Short, ByVal fontheight_Renamed As Short, ByVal rotation As Short, ByVal fontstyle As Short, ByVal fontunderline As Short, ByVal FaceName As String, ByVal TextContent As String)
    Private Declare Sub windowsfontUnicode Lib "tsclib.dll" (ByVal X As Short, ByVal Y As Short, ByVal fontheight_Renamed As Short, ByVal rotation As Short, ByVal fontstyle As Short, ByVal fontunderline As Short, ByVal FaceName As String, ByVal TextContent As Byte())
    Private Declare Sub sendBinaryData Lib "tsclib.dll" (ByVal TextContent As Byte(), ByVal length As Integer)
    Private Declare Function usbportqueryprinter Lib "tsclib.dll" () As Byte

    Sub execute() Handles MyBase.Loaded
        ' print("165", "test", "1")
        CsvToDataTable()
        For Each L As DataRow In print_tab.Rows
            Try
                print(L.Item("cab"), L.Item("text"), L.Item("copy"))
            Catch ex As Exception

            End Try

        Next
        Me.Close()
    End Sub


    Shared Sub print(ByVal cab As String, text As String, copy As String)

        Dim B1 As String = cab
        Dim WT1 As String = text
        Dim status As Byte = 0

        status = usbportqueryprinter() '0 = idle, 1 = head open, 16 = pause, following <ESC>!? command of TSPL manual
        Call openport("TSC TTP-244 Plus")
        Call sendcommand("SIZE 30 mm, 15 mm")
        Call sendcommand("SPEED 4")
        Call sendcommand("DENSITY 12")
        Call sendcommand("DIRECTION 1")
        Call sendcommand("SET TEAR ON")
        Call sendcommand("CODEPAGE UTF-8")
        Call clearbuffer()

        Call barcode("10", "25", "128", "40", "1", "0", "2", "2", B1)
        Call printerfont("10", "95", "0", "0", "10", "10", WT1)
        Call printlabel("1", copy)
        Call closeport()

    End Sub
    Shared Sub CsvToDataTable()
        Dim CSV As String = My.Computer.FileSystem.ReadAllText("C:\ProgramData\Miura\lab_print.csv")
        Try


            'LINES Split
            Dim arrLines() As String
            arrLines = Split(CSV, vbCrLf)
            Dim Linecount As Integer = Split(CSV, vbCrLf).Length


            ' GENERAT HEADER
            Dim HdrColumen() As String
            HdrColumen = Split(arrLines(0), ";")
            For i As Integer = 0 To Split(arrLines(0), ";").Length - 2
                print_tab.Columns.Add(HdrColumen(i))
            Next


            'FILL THE TAB ROW BY ROW
            For n As Integer = 1 To Linecount - 1
                Dim xColumen() As String
                xColumen = Split(arrLines(n), ";")
                Dim xRow As DataRow = print_tab.NewRow
                For x As Integer = 0 To (Split(arrLines(n), ";").Length - 2)
                    xRow.Item(x) = xColumen(x)
                Next
                print_tab.Rows.Add(xRow)
            Next



        Catch ex As Exception
            MessageBox.Show(ex.Message)
        End Try
    End Sub
End Class
