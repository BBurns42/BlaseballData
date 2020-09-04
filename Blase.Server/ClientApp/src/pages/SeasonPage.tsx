﻿import {useParams} from "react-router";
import React, {useEffect} from 'react';

import {DayTable} from "../components/DayTable";
import {Loading} from "../components/Loading";
import {Container} from "../components/Container";
import {Alert, AlertIcon, Heading} from "@chakra-ui/core";
import {useGameList} from "../blaseball/api";
import InfiniteScroll from "react-infinite-scroll-component";
import {cache} from "swr";

function GamesListFetching(props: {season: number}) {
    let { days, error, nextPage } = useGameList(props.season, 10);
    if (error) return <Alert status="error"><AlertIcon/>{error}</Alert>;
    
    if (!days)
        return <Loading/>;
    
    const lastDay = days[days.length - 1].day;
    
    return (
        <InfiniteScroll
            next={nextPage}
            hasMore={lastDay > 0}
            loader={<Loading />}
            dataLength={days.length}
            scrollThreshold="500px"
        >
            {days.map(({games, season, day}) => {
                return <DayTable key={day} season={season+1} day={day + 1} games={games}/>;
            })}
        </InfiniteScroll>
    );
}

export function SeasonPage() {
    let {season} = useParams();
    season = parseInt(season);
    
    // Never reuse caches across multiple seasons, then it feels slower because instant rerender...
    useEffect(() => cache.clear(), [season]);
    
    return (
        <Container my={4}>
            <Heading>Games in Season {season}</Heading>
            
            <GamesListFetching season={season} />
        </Container>
    )
}