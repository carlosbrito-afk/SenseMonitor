using System;
using System.Collections.Generic;
using System.Reflection;

namespace Forja.Tests
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class TestAttribute : Attribute
    {
    }

    public class AssertionException : Exception
    {
        public AssertionException(string message) : base(message)
        {
        }
    }

    public static class Assert
    {
        public static void Equal(object expected, object actual)
        {
            if (!object.Equals(expected, actual))
            {
                throw new AssertionException("expected <" + Show(expected) + "> but was <" + Show(actual) + ">");
            }
        }

        public static void True(bool condition, string msg)
        {
            if (!condition)
            {
                throw new AssertionException(msg);
            }
        }

        public static void Null(object value)
        {
            if (value != null)
            {
                throw new AssertionException("expected null but was <" + Show(value) + ">");
            }
        }

        public static T Throws<T>(Action action) where T : Exception
        {
            try
            {
                action();
            }
            catch (T ex)
            {
                return ex;
            }
            catch (Exception ex)
            {
                throw new AssertionException("expected " + typeof(T).Name + " but got " + ex.GetType().Name);
            }
            throw new AssertionException("expected " + typeof(T).Name + " but nothing was thrown");
        }

        private static string Show(object o)
        {
            return o == null ? "null" : o.ToString();
        }
    }

    public static class TestRunner
    {
        public static int Main(string[] args)
        {
            int passed = 0;
            List<string> failures = new List<string>();
            Type[] types;
            try
            {
                types = Assembly.GetExecutingAssembly().GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                Console.WriteLine("FAIL (type load): " + ex.Message);
                foreach (Exception le in ex.LoaderExceptions)
                {
                    Console.WriteLine("  " + le.Message);
                }
                return 1;
            }
            foreach (Type type in types)
            {
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (method.GetCustomAttributes(typeof(TestAttribute), false).Length == 0)
                    {
                        continue;
                    }
                    string name = type.Name + "." + method.Name;
                    if (method.GetParameters().Length != 0 || method.ReturnType != typeof(void))
                    {
                        failures.Add(name);
                        Console.WriteLine("FAIL " + name + ": invalid test signature (must be public static void with no parameters)");
                        continue;
                    }
                    try
                    {
                        method.Invoke(null, null);
                        passed++;
                        Console.WriteLine("PASS " + name);
                    }
                    catch (TargetInvocationException tie)
                    {
                        failures.Add(name);
                        Exception inner = tie.InnerException;
                        if (inner is AssertionException)
                        {
                            Console.WriteLine("FAIL " + name + ": " + inner.Message);
                        }
                        else
                        {
                            Console.WriteLine("FAIL " + name + ": " + inner);
                        }
                    }
                    catch (Exception ex)
                    {
                        failures.Add(name);
                        Console.WriteLine("FAIL " + name + ": " + ex);
                    }
                }
            }
            Console.WriteLine(passed + " passed, " + failures.Count + " failed");
            return Math.Min(failures.Count, 255);
        }
    }
}
