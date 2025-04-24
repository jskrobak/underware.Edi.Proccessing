namespace underware.Edi.Processing;

public class ParseException: Exception
{
    public ParseException(string message, Exception innerEx): base(message, innerEx)
    {
        
    }
}