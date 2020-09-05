import { GameUpdate } from "./update";
import { Day } from "./game";
import useSWR, {useSWRInfinite} from "swr";
import {useEffect, useState} from "react";

export interface GamesResponse {
    days: Day[]
}

export interface GameUpdatesResponse {
    updates: GameUpdate[]
}

export function useGameList(season: number, pageSize: number) {
    function getNextPage(pageIndex: number, previousPageData: GamesResponse | null) {
        let startDay = 999;
        if (previousPageData) {
            const {days} = previousPageData;
            const lastDay = days[days.length-1];
            startDay = lastDay.day - 1;
        }

        if (startDay < 0)
            // at the end! :)
            return null;

        return `/api/games?season=${season-1}&day=${startDay}&dayCount=${pageSize}&reverse=true`
    }

    const { data, size, setSize, error } = useSWRInfinite<GamesResponse>(getNextPage, {
        revalidateOnFocus: false
    });
    
    const days = [];
    for (const page of (data ?? []))
        days.push(...page.days);
    
    return {
        days: data ? days : null, 
        error, 
        pageCount: size,
        nextPage: () => setSize(size + 1)
    }
}

interface GameUpdatesHookReturn {
    updates: GameUpdate[];
    error: any;
    isLoading: boolean;
}

export function useGameUpdates(game: string, autoRefresh: boolean): GameUpdatesHookReturn {
    // First load of original data
    const { data: initialData, error } = useSWR<GameUpdatesResponse>(`/api/games/${game}/updates`,  {revalidateOnFocus: false});
    
    // Updates added via autoupdating
    const [extraUpdates, setExtraUpdates] = useState<GameUpdate[]>([]);
    
    // Combined the above!
    const allUpdates = [...(initialData?.updates ?? []), ...extraUpdates];
    
    // Background timer for autoupdating
    useEffect(() => {
        const timer = setInterval(async () => {
            // Stop if autorefresh is off
            // (effect closure will get remade on change so this "updates" properly)
            if (!autoRefresh || allUpdates.length == 0)
                return;
            
            // Handle autorefresh logic
            const lastUpdate = allUpdates[allUpdates.length - 1];
            const lastTimestamp = lastUpdate.timestamp;
            
            const url = `/api/games/${game}/updates?after=${encodeURIComponent(lastTimestamp)}`;
            const response = await fetch(url);
            const json = <GameUpdatesResponse>(await response.json());
            
            // Add the data we got to the extra state :)
            setExtraUpdates([...extraUpdates, ...json.updates])
        }, 2000);
        return () => clearInterval(timer);
    }, [game, autoRefresh, allUpdates.length])
    
    return {
        updates: allUpdates,
        isLoading: !initialData,
        error
    }
}