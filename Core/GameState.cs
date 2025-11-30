using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core;
public class GameState
{
    private static readonly Lazy<GameState> _instance =
    new Lazy<GameState>(() => new GameState());

    /// <summary>
    /// Singleton access for global game state
    /// </summary>
    public static GameState Instance => _instance.Value;

    private GameState()
    {

    }

}