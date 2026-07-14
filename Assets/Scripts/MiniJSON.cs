using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.IO;

namespace MiniJSON
{
    public static class Json
    {
        public static string Serialize(object obj)
        {
            return SerializeValue(obj);
        }
        
        private static string SerializeValue(object value)
        {
            if (value == null) return "null";
            
            if (value is string) return "\"" + value.ToString() + "\"";
            if (value is bool) return value.ToString().ToLower();
            if (value is float || value is double) return value.ToString();
            if (value is int || value is long) return value.ToString();
            
            if (value is Dictionary<string, object> dict)
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("{");
                bool first = true;
                foreach (var kvp in dict)
                {
                    if (!first) sb.Append(",");
                    sb.Append("\"").Append(kvp.Key).Append("\":");
                    sb.Append(SerializeValue(kvp.Value));
                    first = false;
                }
                sb.Append("}");
                return sb.ToString();
            }
            
            if (value is Dictionary<string, float> dictFloat)
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("{");
                bool first = true;
                foreach (var kvp in dictFloat)
                {
                    if (!first) sb.Append(",");
                    sb.Append("\"").Append(kvp.Key).Append("\":");
                    sb.Append(kvp.Value);
                    first = false;
                }
                sb.Append("}");
                return sb.ToString();
            }
            
            return value.ToString();
        }
        
        public static object Deserialize(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            
            Parser parser = new Parser(json);
            return parser.ParseValue();
        }
        
        sealed class Parser
        {
            const string WHITE_SPACE = " \t\n\r";
            const string WORD_BREAK = " \t\n\r{}[],:\"";
            
            StringReader json;
            
            public Parser(string jsonString)
            {
                json = new StringReader(jsonString);
            }
            
            public object ParseValue()
            {
                TOKEN nextToken = NextToken;
                
                switch (nextToken)
                {
                    case TOKEN.STRING:
                        return ParseString();
                    case TOKEN.NUMBER:
                        return ParseNumber();
                    case TOKEN.CURLY_OPEN:
                        return ParseObject();
                    case TOKEN.TRUE:
                        json.Read();
                        return true;
                    case TOKEN.FALSE:
                        json.Read();
                        return false;
                    case TOKEN.NULL:
                        json.Read();
                        return null;
                    default:
                        return null;
                }
            }
            
            Dictionary<string, object> ParseObject()
            {
                Dictionary<string, object> table = new Dictionary<string, object>();
                
                json.Read(); // {
                
                while (true)
                {
                    TOKEN token = NextToken;
                    
                    switch (token)
                    {
                        case TOKEN.NONE:
                            return null;
                        case TOKEN.CURLY_CLOSE:
                            return table;
                        default:
                            string name = ParseString();
                            if (name == null) return null;
                            
                            if (NextToken != TOKEN.COLON) return null;
                            json.Read();
                            
                            table[name] = ParseValue();
                            break;
                    }
                }
            }
            
            string ParseString()
            {
                StringBuilder s = new StringBuilder();
                char c;
                
                json.Read(); // "
                
                bool parsing = true;
                while (parsing)
                {
                    if (json.Peek() == -1) break;
                    
                    c = (char)json.Read();
                    switch (c)
                    {
                        case '"':
                            parsing = false;
                            break;
                        case '\\':
                            if (json.Peek() == -1)
                            {
                                parsing = false;
                                break;
                            }
                            
                            c = (char)json.Read();
                            s.Append(c);
                            break;
                        default:
                            s.Append(c);
                            break;
                    }
                }
                
                return s.ToString();
            }
            
            object ParseNumber()
            {
                string number = NextWord;
                
                if (number.IndexOf('.') == -1)
                {
                    long parsedInt;
                    Int64.TryParse(number, out parsedInt);
                    return parsedInt;
                }
                
                double parsedDouble;
                Double.TryParse(number, out parsedDouble);
                return parsedDouble;
            }
            
            void EatWhitespace()
            {
                while (WHITE_SPACE.IndexOf(PeekChar) != -1)
                {
                    json.Read();
                    
                    if (json.Peek() == -1) break;
                }
            }
            
            char PeekChar
            {
                get { return Convert.ToChar(json.Peek()); }
            }
            
            string NextWord
            {
                get
                {
                    StringBuilder word = new StringBuilder();
                    
                    while (WORD_BREAK.IndexOf(PeekChar) == -1)
                    {
                        word.Append((char)json.Read());
                        
                        if (json.Peek() == -1) break;
                    }
                    
                    return word.ToString();
                }
            }
            
            TOKEN NextToken
            {
                get
                {
                    EatWhitespace();
                    
                    if (json.Peek() == -1) return TOKEN.NONE;
                    
                    char c = PeekChar;
                    switch (c)
                    {
                        case '{':
                            return TOKEN.CURLY_OPEN;
                        case '}':
                            json.Read();
                            return TOKEN.CURLY_CLOSE;
                        case ':':
                            return TOKEN.COLON;
                        case '"':
                            return TOKEN.STRING;
                        case '-':
                        case '0':
                        case '1':
                        case '2':
                        case '3':
                        case '4':
                        case '5':
                        case '6':
                        case '7':
                        case '8':
                        case '9':
                            return TOKEN.NUMBER;
                    }
                    
                    string word = NextWord;
                    
                    switch (word)
                    {
                        case "false":
                            return TOKEN.FALSE;
                        case "true":
                            return TOKEN.TRUE;
                        case "null":
                            return TOKEN.NULL;
                    }
                    
                    return TOKEN.NONE;
                }
            }
        }
        
        enum TOKEN
        {
            NONE,
            CURLY_OPEN,
            CURLY_CLOSE,
            COLON,
            STRING,
            NUMBER,
            TRUE,
            FALSE,
            NULL
        }
    }
}